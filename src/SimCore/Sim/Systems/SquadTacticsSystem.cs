using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// INF-E1b-3, the squad think (docs/design.md, "Infantry combat: the squad think"; the game repo's docs/plans/infantry-battle-rules.md
// sections 10.1 to 10.3, 11.1 to 11.4, 11.6, 15.4, 15.6, 15.7, 16.2, 19.3, 19.4 and 21): the enemy group, the force ratio, the
// engagement choice, the contact line and its frontage boxes, elements, bounds and marching fire, suppressive fire, the focus
// target, the LMG's deploy. A pass after the soldiers' thinks (RunMinds), squads in ID order; every loop capped by a squad's
// members or the picture's 16 entries. The squad's moves all go through INF-MOVE's members' walk (FollowSlots, SquadSystem.cs):
// each member's goal is picked at the think and handed out in line order (no crossings), and they walk to it together by pace.
public sealed partial class World
{
    private const ulong SuppressSalt = 0x1E63_0000_0000_0001UL; // 11.6 where a suppressive shot lands
    private const ulong SuppressHitSalt = 0x1E63_0000_0000_0002UL; // 11.6 the quarter-chance hit roll on a man at the aim point

    private readonly List<(long Key, uint Id)> _lineOrder = []; // scratch: members by place along the line
    private readonly List<Cell> _lineTaken = []; // scratch: slots and goals handed out this think
    private int _squadUrgentThisTick;

    internal (long Thinks, long Urgent, long Lines, long Bounds, long Suppressive) TacticsStats;

    // RunSquadMinds (21): after the soldiers' thinks, squads in ID order, each on (ID mod squadEvery) or the next tick when urgent
    // (at most squad.urgentThinks a tick; the rest wait for their own); then each LMG gunner's deploy (16.2).
    private void RunSquadMinds(InfantryCombatRules r)
    {
        _squadUrgentThisTick = 0;
        var every = (uint)r[CombatValue.SquadEvery];
        foreach (var s in _squads)
        {
            if (s.Mind is not { } m)
            {
                continue;
            }
            var due = s.Id % every == Tick % every;
            if (!due && m.Urgent && _squadUrgentThisTick < r[CombatValue.SqUrgentThinks])
            {
                _squadUrgentThisTick++;
                TacticsStats.Urgent++;
                due = true;
            }
            if (due)
            {
                m.Urgent = false;
                ThinkTactics(s, m, r);
            }
        }
        foreach (var u in _units)
        {
            if (u.Fight is { Lmg: true } f)
            {
                TickLmg(u, f, r);
            }
        }
    }

    // PlayerOrdered (19.4): any order of the player ends every automatic behaviour of the squad; the table runs again at the next tick.
    private void PlayerOrdered(Squad s)
    {
        if (s.Mind is not { } m)
        {
            return;
        }
        if (m.HasLine)
        {
            ReAnchor(s); // the order goes from where the squad stands, not from where its line was built
        }
        ClearTactics(s, m);
        (m.Urgent, m.OrderNew) = (true, true);
    }

    // ClearTactics drops the line, the bounds, the march and the members' suppressive fire; the choice goes back to none.
    private void ClearTactics(Squad s, SquadMind m)
    {
        if (m.HasLine)
        {
            s.FinalsStale = true;
        }
        m.LineList.Clear();
        (m.HasLine, m.LineAxis, m.Moving, m.Last, m.Marching) = (false, -1, 0, 0, false);
        (m.Choice, m.ChoiceR, m.Ratio) = (SquadChoice.None, 0, 0);
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Fight: { } f })
            {
                f.Suppressing = false;
            }
        }
    }

    // DropLine: the line, bounds and march go; a squad on an order goes on with it from where it stands (its path planned again).
    private void DropLine(Squad s, SquadMind m)
    {
        if (!m.HasLine)
        {
            return;
        }
        if (s.Order != OrderKind.Idle && !s.Loose)
        {
            ReAnchor(s);
            ClearSquadPath(s);
            s.PathPending = true;
        }
        s.FinalsStale = true;
        m.LineList.Clear();
        (m.HasLine, m.LineAxis, m.Moving, m.Last, m.Marching) = (false, -1, 0, 0, false);
    }

    // Release: the squad is calm again (or the rules don't plan for it): its line goes, and a squad on an order goes on with it from
    // where it stands (its path planned again from there: INF-MOVE's final positions at the goal, no walk back to the line's start).
    private void Release(Squad s, SquadMind m)
    {
        DropLine(s, m);
        if (m.Choice != SquadChoice.None || m.OpenedFire || m.Alert)
        {
            ClearTactics(s, m);
        }
        (m.Alert, m.OpenedFire, m.GroupCount, m.Main) = (false, false, 0, 0);
    }

    // Manoeuvres (19.3): the squad's own moves (the line, bounds, marching fire) only in Guard and Aggressive: the leader's stance.
    private bool Manoeuvres(Squad s) => Unit(s.Leader) is { Stance: Stance.Guard or Stance.Aggressive };

    // ThinkTactics: one squad think (10.1).
    private void ThinkTactics(Squad s, SquadMind m, InfantryCombatRules r)
    {
        TacticsStats.Thinks++;
        if (s.Loose || s.Retreating || !SquadCentre(s, out var centre))
        {
            Release(s, m);
            return;
        }
        var able = 0;
        foreach (var id in s.MemberList)
        {
            able += Unit(id) is { } u && Able(u) ? 1 : 0;
        }
        var lost = m.AbleCount > 0 && able < m.AbleCount;
        m.AbleCount = able;
        var ar = (long)r[CombatValue.AlertRange] * Fixed.OneRaw / 10;
        var alert = false;
        foreach (var k in m.PictureList)
        {
            alert |= (k.At.Center() - centre.Center()).LenSq() <= ar * ar;
        }
        if (m.InContact || alert)
        {
            m.HotTick = Tick;
        }
        else if (m.HotTick == 0 || Tick - m.HotTick >= (uint)r[CombatValue.SqCalmTicks])
        {
            Release(s, m);
            return;
        }
        m.Alert = alert && !m.InContact;
        EnemyGroup(s, m, centre, r);
        if (m.GroupCount == 0)
        {
            return; // nothing known to fight yet: the line and bounds stand as they are until calm
        }
        if (m.InContact)
        {
            ForceRatio(s, m, r);
            Choose(s, m, centre, r);
        }
        NameFocus(s, m, r);
        ThinkLine(s, m, centre, lost, r);
        if (m.HasLine && m.Choice == SquadChoice.Advance)
        {
            Advance(s, m, centre, r);
        }
        else if (m.Moving != 0 || m.Marching)
        {
            EndBound(s, m, true); // the choice changed: the movers finish to their goals (11.3 step 6), the march stops
        }
        OrderSuppression(s, m, r);
        m.OrderNew = false;
    }

    // EnemyGroup (10.1): the main enemy is the picture's contact of the highest danger (ties: nearest the centre, then the lower ID),
    // the old one kept while known and within mainKeep% of the new one's; the group is it and the contacts within groupRadius of it.
    private void EnemyGroup(Squad s, SquadMind m, Cell centre, InfantryCombatRules r)
    {
        int bestI = -1, bestD = -1, oldI = -1, oldD = 0;
        long bestD2 = 0;
        for (var i = 0; i < m.PictureList.Count; i++)
        {
            var k = m.PictureList[i];
            var d = Danger(k, null, r);
            var d2 = (k.At.Center() - centre.Center()).LenSq();
            if (d > bestD || (d == bestD && (d2 < bestD2 || (d2 == bestD2 && k.Enemy < m.PictureList[bestI].Enemy))))
            {
                (bestI, bestD, bestD2) = (i, d, d2);
            }
            if (m.Main != 0 && k.Enemy == m.Main)
            {
                (oldI, oldD) = (i, d);
            }
        }
        if (bestI < 0)
        {
            (m.Main, m.GroupCount) = (0, 0);
            return;
        }
        if (oldI >= 0 && (long)oldD * 100 >= (long)bestD * r[CombatValue.SqMainKeep])
        {
            bestI = oldI;
        }
        var main = m.PictureList[bestI];
        m.Main = main.Enemy;
        var gr = (long)r[CombatValue.SqGroupRadius] * Fixed.OneRaw / 10;
        long sx = 0, sy = 0;
        int n = 0, pinned = 0;
        uint seen = 0;
        foreach (var k in m.PictureList)
        {
            if ((k.At.Center() - main.At.Center()).LenSq() > gr * gr)
            {
                continue;
            }
            (sx, sy, n) = (sx + k.At.X, sy + k.At.Y, n + 1);
            seen = Math.Max(seen, k.Tick);
            pinned += k.Enemy != 0 && Unit(k.Enemy) is { Fight.Suppression: var sup } && sup >= r[CombatValue.Pinned] ? 1 : 0; // the engine reads the real value (10.3)
        }
        (m.GroupAt, m.GroupCount, m.GroupPinned, m.GroupSeen) = (new Cell((int)(sx / n), (int)(sy / n)), n, pinned, seen);
    }

    // InGroup: contact k lies within groupRadius of the group's centre.
    private static bool InGroup(SquadMind m, Contact k, long gr) => (k.At.Center() - m.GroupAt.Center()).LenSq() <= gr * gr * 4;

    // ---- 10.2: the force ratio ----

    // ForceValue: the weapon class's value at distance d (raw) against infantry or vehicles (10.2's table), 0 beyond 1.5 x its range.
    private static int ForceValue(CombatClass? c, long d, long range, bool vehicles, InfantryCombatRules r)
    {
        var inRange = d <= range;
        if (!inRange && d * 2 > range * 3)
        {
            return 0;
        }
        return c switch
        {
            CombatClass.Rifle => vehicles ? 0 : inRange ? r[CombatValue.SqRifle] : r[CombatValue.SqRifleOut],
            CombatClass.Carbine => vehicles ? 0 : inRange ? r[CombatValue.SqCarbine] : r[CombatValue.SqCarbineOut],
            CombatClass.Smg => vehicles ? 0 : inRange ? r[CombatValue.SqSmg] : r[CombatValue.SqSmgOut],
            CombatClass.Lmg => vehicles ? 0 : inRange ? r[CombatValue.SqLmg] : r[CombatValue.SqLmgOut],
            CombatClass.Marksman => vehicles ? 0 : inRange ? r[CombatValue.SqMarksman] : r[CombatValue.SqMarksmanOut],
            CombatClass.Pistol => vehicles || !inRange ? 0 : r[CombatValue.SqPistol],
            CombatClass.Launcher => !inRange ? 0 : vehicles ? r[CombatValue.SqLauncherVehicle] : r[CombatValue.SqLauncher],
            _ => !inRange ? 0 : vehicles ? r[CombatValue.SqKornetVehicle] : r[CombatValue.SqKornet], // the crews' heavy arms (the Kornet's row)
        };
    }

    // ForceRatio (10.2): R = 100 x F(us) x (100 - their protection) / (F(them) x (100 - ours)), clamped, moving at most ratioStep a think.
    private void ForceRatio(Squad s, SquadMind m, InfantryCombatRules r)
    {
        var gr = (long)r[CombatValue.SqGroupRadius] * Fixed.OneRaw / 10;
        long themInf = 0, themVeh = 0, theirProt = 0;
        int theirN = 0;
        var mainId = m.Main;
        foreach (var k in m.PictureList)
        {
            var near = long.MaxValue;
            foreach (var id in s.MemberList)
            {
                if (Unit(id) is { } u && Able(u))
                {
                    near = Math.Min(near, (u.Pos - k.At.Center()).Len().Raw);
                }
            }
            var weight = k.Source == ContactSource.SeenNow ? 100 : k.Source == ContactSource.Heard ? r[CombatValue.SqHeard] : r[CombatValue.SqSeenBefore];
            if (k.Enemy == 0)
            {
                themInf += r[CombatValue.SqArea] * 100;
                continue;
            }
            var reach = ReachOf(k, r, out var range);
            if (k.Class is CombatClass.LightVehicle or CombatClass.Armour or CombatClass.Helicopter)
            {
                themVeh += (near <= reach ? Danger(k with { Source = ContactSource.SeenNow, FiringAtUs = false }, null, r) : 0) * weight;
            }
            else
            {
                themInf += ForceValue(k.Class, near, Math.Max(1, range), false, r) * (long)weight;
            }
            if (k.Source == ContactSource.SeenNow && Unit(k.Enemy) is { Fight: { } ef } e && Unit(s.Leader) is { } l)
            {
                theirProt += Protection(Cell.Of(e.Pos), ef.Posture, Dir32(l.Pos.X.Raw - e.Pos.X.Raw, l.Pos.Y.Raw - e.Pos.Y.Raw), false);
                theirN++;
            }
        }
        var vehicles = themVeh * 2 >= themInf + themVeh && themVeh > 0;
        var them = vehicles ? themVeh : themInf;
        long us = 0, ourProt = 0;
        var ourN = 0;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is not { Fight: { } f } u || !Able(u) || u.Def.Weapons.Count == 0)
            {
                continue;
            }
            var near = long.MaxValue;
            foreach (var k in m.PictureList)
            {
                if (InGroup(m, k, gr))
                {
                    near = Math.Min(near, (u.Pos - k.At.Center()).Len().Raw);
                }
            }
            var w = u.Def.Weapons[u.Arms is { } a ? a.InHand : 0];
            var cls = r.Weapon(w.Effect)?.Class;
            long v = cls == CombatClass.Lmg && f.Deployed && near <= w.Range.Raw ? r[CombatValue.SqLmgDeployed] : ForceValue(cls, near, Math.Max(1, w.Range.Raw), vehicles, r);
            v = u.Life is { Wound: WoundState.Wounded } ? v * r[CombatValue.SqWounded] / 100 : v;
            v = f.Suppression >= r[CombatValue.PinnedHard] ? v * r[CombatValue.SqPinnedHard] / 100 : f.Suppression >= r[CombatValue.Pinned] ? v * r[CombatValue.SqSuppressed] / 100 : v;
            v = OutOfAmmo(u) ? v * r[CombatValue.SqEmpty] / 100 : LowAmmo(u) ? v * r[CombatValue.SqLowAmmo] / 100 : v;
            v = u.Life?.Rank switch
            {
                SoldierRank.Regular => v * r[CombatValue.SqRankRegular] / 100,
                SoldierRank.Veteran => v * r[CombatValue.SqRankVeteran] / 100,
                SoldierRank.Elite => v * r[CombatValue.SqRankElite] / 100,
                _ => v,
            };
            us += v * 100;
            if (m.Axis >= 0)
            {
                ourProt += Protection(Cell.Of(u.Pos), f.Posture, m.Axis, false);
                ourN++;
            }
        }
        var theirP = theirN > 0 ? theirProt / theirN : 0;
        var ourP = ourN > 0 ? ourProt / ourN : 0;
        long raw = them <= 0 ? r[CombatValue.SqRatioMax] : us <= 0 ? r[CombatValue.SqRatioMin] :
            100 * us * (100 - theirP) / Math.Max(1, them * (100 - ourP));
        var ratio = (int)Math.Clamp(raw, r[CombatValue.SqRatioMin], r[CombatValue.SqRatioMax]);
        var step = r[CombatValue.SqRatioStep];
        m.Ratio = m.Ratio == 0 ? ratio : Math.Clamp(ratio, m.Ratio - step, m.Ratio + step);
        _ = mainId;
    }

    // ---- 10.3: the engagement choice ----

    // LineRange: the range of the squad's line weapon (its first able Line member's main arm; any member's without one).
    private long SquadLineRange(Squad s)
    {
        long any = 0;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is not { } u || !Able(u) || u.Def.Weapons.Count == 0)
            {
                continue;
            }
            if (SquadRoleOf(u) == SquadRole.Line)
            {
                return u.Def.Weapons[0].Range.Raw;
            }
            any = Math.Max(any, u.Def.Weapons[0].Range.Raw);
        }
        return any;
    }

    // GroupNear: the distance (raw) from the nearest able member to the nearest contact of the enemy group.
    private long GroupNear(Squad s, SquadMind m, InfantryCombatRules r)
    {
        var gr = (long)r[CombatValue.SqGroupRadius] * Fixed.OneRaw / 10;
        var best = long.MaxValue;
        foreach (var k in m.PictureList)
        {
            if (!InGroup(m, k, gr))
            {
                continue;
            }
            foreach (var id in s.MemberList)
            {
                if (Unit(id) is { } u && Able(u))
                {
                    best = Math.Min(best, (u.Pos - k.At.Center()).Len().Raw);
                }
            }
        }
        return best;
    }

    // Choose (10.3): the first matching row, orders first; a choice kept choiceHoldTicks unless urgent (a new order); a threshold on R
    // passed back by choiceHysteresis to leave it. Part 1 builds Advance and Fight in place; Flank, Assault and Withdraw are part 2's.
    private void Choose(Squad s, SquadMind m, Cell centre, InfantryCombatRules r)
    {
        var hyst = r[CombatValue.SqChoiceHysteresis];
        int Bar(int t) => m.Choice == SquadChoice.Advance && m.ChoiceR == t ? t - hyst : t;
        var near = GroupNear(s, m, r);
        var closed = near <= SquadLineRange(s) * r[CombatValue.SqAdvanceToPercent] / 100;
        var want = SquadChoice.FightInPlace;
        var thr = 0;
        if (s.Mode is SquadMode.Hold or SquadMode.Ambush)
        {
            want = SquadChoice.FightInPlace; // never Advance, Flank or Assault
        }
        else if (s.Order == OrderKind.Move)
        {
            want = SquadChoice.Advance; // the order: INF-MOVE's squad move carries it (no line: decision 2, obey)
        }
        else if (s.Order == OrderKind.AttackMove)
        {
            var inReach = false;
            foreach (var id in s.MemberList)
            {
                inReach |= Unit(id) is { AutoTarget: not 0 } u && Able(u);
            }
            if (!inReach)
            {
                want = SquadChoice.Advance; // along the order's way, by bounds while in contact
            }
            else if (m.Ratio >= Bar(r[CombatValue.SqAdvanceR]) && !closed)
            {
                (want, thr) = (SquadChoice.Advance, r[CombatValue.SqAdvanceR]);
            }
        }
        else if (s.Order == OrderKind.Idle && Manoeuvres(s) && m.Ratio >= Bar(r[CombatValue.SqIdleAdvanceR]) && !closed &&
            near * 2 <= SquadLineRange(s) * 3 && Within(centre.Center(), s.Anchor, Fixed.FromRaw((long)r[CombatValue.SqPostLeash] * Fixed.OneRaw / 10)))
        {
            (want, thr) = (SquadChoice.Advance, r[CombatValue.SqIdleAdvanceR]); // up to postLeash from its post, then fight in place
        }
        if (want == m.Choice)
        {
            return;
        }
        var urgent = want is SquadChoice.Withdraw or SquadChoice.Assault || m.OrderNew || m.Choice == SquadChoice.None;
        if (!urgent && Tick - m.ChoiceTick < (uint)r[CombatValue.SqChoiceHoldTicks])
        {
            return;
        }
        (m.Choice, m.ChoiceTick, m.ChoiceR) = (want, Tick, thr);
    }

    // ---- 15.4: the focus target ----

    // NameFocus (15.4): an enemy LMG gunner deployed within his range of a member, a launcher or Kornet whose target is a friendly
    // vehicle, or a marksman who fired at the squad within marksmanTicks and is seen now; kept until down, gone or unseen focusLostTicks.
    private void NameFocus(Squad s, SquadMind m, InfantryCombatRules r)
    {
        if (m.Focus != 0)
        {
            var seenNow = false;
            foreach (var k in m.PictureList)
            {
                seenNow |= k.Enemy == m.Focus && k.Source == ContactSource.SeenNow;
            }
            if (seenNow)
            {
                m.FocusSeen = Tick;
            }
            if (Unit(m.Focus) is not { Alive: true } fe || fe.Life is { Wound: WoundState.Down } || Tick - m.FocusSeen >= (uint)r[CombatValue.TargetFocusLostTicks])
            {
                m.Focus = 0;
            }
            else
            {
                return;
            }
        }
        foreach (var k in m.PictureList)
        {
            if (k.Enemy == 0 || k.Source != ContactSource.SeenNow || Unit(k.Enemy) is not { Alive: true } e || e.Life is { Wound: WoundState.Down })
            {
                continue;
            }
            var name = k.Class switch
            {
                CombatClass.Lmg => e.Fight is { Deployed: true } && e.Def.Weapons.Count > 0 && NearestMember(s, e.Pos) <= e.Def.Weapons[0].Range.Raw,
                CombatClass.Launcher or CombatClass.Crew => Unit(e.AutoTarget) is { Life: null } vt && vt.Owner == s.Owner,
                CombatClass.Marksman => e.Fight is { } mf && mf.LastTarget != 0 && Tick - mf.LastTargetTick < (uint)r[CombatValue.TargetMarksmanTicks] &&
                    Unit(mf.LastTarget) is { Squad: var sq } && sq == s.Id,
                _ => false,
            };
            if (name)
            {
                (m.Focus, m.FocusSeen) = (e.Id, Tick);
                return;
            }
        }
    }

    private long NearestMember(Squad s, Vec p)
    {
        var best = long.MaxValue;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { } u && Able(u))
            {
                best = Math.Min(best, (u.Pos - p).Len().Raw);
            }
        }
        return best;
    }

    // ---- 11.1: the contact line and the frontage ----

    // LineIndex: the member's place in the squad's line, or -1.
    private static int LineIndex(SquadMind m, uint unit)
    {
        int lo = 0, hi = m.LineList.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var id = m.LineList[mid].Unit;
            if (id == unit)
            {
                return mid;
            }
            (lo, hi) = id < unit ? (mid + 1, hi) : (lo, mid - 1);
        }
        return -1;
    }

    // LineGoalOf: where member u walks for the squad's line (his goal), or null (no line, or no place for him: the recon is free).
    internal Vec? LineGoalOf(Squad s, Unit u) =>
        s.Mind is { HasLine: true } m && LineIndex(m, u.Id) is >= 0 and var i ? m.LineList[i].Goal.Center() : null;

    // Bounding: u is on a bound or the march (11.3, 11.4): the squad chose his spot, his own position choice waits (6.1 not applied).
    internal bool Bounding(Unit u) =>
        u.Squad != 0 && Squad(u.Squad) is { Mind: { HasLine: true } m } && LineIndex(m, u.Id) is >= 0 and var i && m.LineList[i].Bounding;

    // InBox (11.1): cell c lies in u's frontage box: his slot +- boxAlong along the line, boxBehind behind it to boxAhead ahead (two
    // integer dot products against the line's axis and line vectors). True without a line.
    internal bool InBox(Unit u, Cell c, InfantryCombatRules r)
    {
        if (u.Squad == 0 || Squad(u.Squad) is not { Mind: { HasLine: true, LineAxis: >= 0 } m } || LineIndex(m, u.Id) is not (>= 0 and var i))
        {
            return true;
        }
        return InBoxOf(m.LineList[i].Slot, c, m.LineAxis, r);
    }

    private static bool InBoxOf(Cell slot, Cell c, int axis, InfantryCombatRules r)
    {
        var (ax, ay) = InfantryCombatDirections(axis);
        long ox = (c.X - slot.X) * 10L, oy = (c.Y - slot.Y) * 10L;
        var ahead = (ox * ax + oy * ay) / 1024;
        var along = (ox * -ay + oy * ax) / 1024;
        return Math.Abs(along) <= r[CombatValue.SqBoxAlong] && ahead >= -r[CombatValue.SqBoxBehind] && ahead <= r[CombatValue.SqBoxAhead];
    }

    private static (int X, int Y) InfantryCombatDirections(int d) => Directions[d & 31];

    // ThinkLine (11.1): the line across the squad's threat axis on becoming alert or in contact (not in Hold or Ambush, which keep
    // their picks; not under a Move, which INF-MOVE carries; not in a still stance, 19.3). Rebuilt when the axis changes, the centre
    // has moved rebuildMoved since the build, or a member is lost; not while a bound runs, unless urgent (urgentSteps off).
    private void ThinkLine(Squad s, SquadMind m, Cell centre, bool lost, InfantryCombatRules r)
    {
        if (s.Mode is SquadMode.Hold or SquadMode.Ambush || s.Order == OrderKind.Move || m.Axis < 0 || !Manoeuvres(s) ||
            (s.Order != OrderKind.Idle && !m.InContact))
        {
            DropLine(s, m); // (an attack-move goes on along its way while only alert: 10.3's "nobody in reach" row)
            return;
        }
        var moved = (long)r[CombatValue.SqRebuildMoved] * Fixed.OneRaw / 10;
        var urgent = m.HasLine && DirSteps(m.Axis, m.LineAxis) >= r[CombatValue.SqUrgentSteps];
        var due = !m.HasLine || lost || m.Axis != m.LineAxis || (centre.Center() - m.LineCentre.Center()).LenSq() >= moved * moved;
        if (!due || (m.Moving != 0 && !urgent))
        {
            return;
        }
        if (urgent && m.Moving != 0)
        {
            EndBound(s, m, false); // a new front on the flank: the bound ends at once, the movers drop where they are
        }
        BuildLine(s, m, centre, r);
    }

    // BuildLine: slots 2 cells apart (1.5 with manyOnLine or more) through the centre across the axis, dealt in the order of the
    // members' place along it (no one crosses another's way; ties by ID); role depths behind it; each slot on the nearest standable
    // cell not taken (ties: row, then column). A member already inside his new box stays where he is (his goal his own cell).
    private void BuildLine(Squad s, SquadMind m, Cell centre, InfantryCombatRules r)
    {
        TacticsStats.Lines++;
        var (ax, ay) = InfantryCombatDirections(m.Axis);
        _lineOrder.Clear();
        var onLine = 0;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is not { } u || !Able(u) || SquadRoleOf(u) == SquadRole.Recon)
            {
                continue;
            }
            var role = SquadRoleOf(u);
            long ox = u.Pos.X.Raw - centre.Center().X.Raw, oy = u.Pos.Y.Raw - centre.Center().Y.Raw;
            _lineOrder.Add(((ox * -ay + oy * ax) >> Fixed.Shift, u.Id));
            onLine += role is SquadRole.Line or SquadRole.Flank ? 1 : 0;
        }
        _lineOrder.Sort((p, q) => p.Key != q.Key ? p.Key.CompareTo(q.Key) : p.Id.CompareTo(q.Id));
        var spacing = (long)(onLine >= r[CombatValue.SqManyOnLine] ? r[CombatValue.SqMinSpacing] : r[CombatValue.SqContactSpacing]);
        var old = m.LineList.ToArray();
        m.LineList.Clear();
        _lineTaken.Clear();
        var k = 0;
        var c0 = centre.Center();
        foreach (var (_, id) in _lineOrder)
        {
            var u = Unit(id)!;
            var role = SquadRoleOf(u);
            long lat = 0, depth = 0;
            if (role is SquadRole.Line or SquadRole.Flank)
            {
                lat = (2 * k - (onLine - 1)) * spacing / 2;
                depth = role == SquadRole.Flank ? r[CombatValue.SqFlankBehind] : 0;
                k++;
            }
            else
            {
                depth = role switch
                {
                    SquadRole.Medic => r[CombatValue.SqMedicBehind],
                    SquadRole.Support => r[CombatValue.SqSupportBehind],
                    _ => r[CombatValue.SqAirGuardBehind],
                };
            }
            var p = new Vec(Fixed.FromRaw(c0.X.Raw + (-ay * lat - ax * depth) * Fixed.OneRaw / 10240), Fixed.FromRaw(c0.Y.Raw + (ax * lat - ay * depth) * Fixed.OneRaw / 10240));
            var slot = SnapFree(Cell.Of(OnMap(p)));
            var here = Cell.Of(u.Pos);
            var still = u.Stance is not (Stance.Guard or Stance.Aggressive);
            if (still)
            {
                slot = here; // a still stance never moves on its own (19.3): he fights from his cell
            }
            var goal = still || InBoxOf(slot, here, m.Axis, r) ? here : slot;
            var el = (byte)0;
            foreach (var o in old)
            {
                el = o.Unit == id ? o.Element : el;
            }
            m.LineList.Add(new LinePlace(id, slot, goal, el, false));
            _lineTaken.Add(slot);
        }
        m.LineList.Sort((p, q) => p.Unit.CompareTo(q.Unit));
        (m.HasLine, m.LineAxis, m.LineCentre) = (true, m.Axis, centre);
    }

    // SnapFree: the nearest standable cell to c within 3 rings that no slot handed out this think took (ties: row, then column).
    private Cell SnapFree(Cell c)
    {
        for (var ring = 0; ring <= 3; ring++)
        {
            for (var dy = -ring; dy <= ring; dy++)
            {
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                    {
                        continue;
                    }
                    var q = new Cell(c.X + dx, c.Y + dy);
                    if (Standable(q) && !_lineTaken.Contains(q))
                    {
                        return q;
                    }
                }
            }
        }
        return c;
    }

    // ---- 11.2 to 11.4: elements, bounds, marching fire ----

    // DealElements (11.2): the able Line, LMG and Flank members by place along the line from its left end, dealt A, B, A, B; the
    // LMG gunner put in B (the nearest B member to A if that leaves them more than one apart). The rear roles are in neither.
    private void DealElements(Squad s, SquadMind m)
    {
        var (ax, ay) = InfantryCombatDirections(m.LineAxis);
        _lineOrder.Clear();
        foreach (var p in m.LineList)
        {
            if (Unit(p.Unit) is { } u && Able(u) && SquadRoleOf(u) is SquadRole.Line or SquadRole.Flank)
            {
                _lineOrder.Add((((long)p.Slot.X * -ay + (long)p.Slot.Y * ax), p.Unit));
            }
        }
        _lineOrder.Sort((p, q) => p.Key != q.Key ? p.Key.CompareTo(q.Key) : p.Id.CompareTo(q.Id));
        var els = new byte[_lineOrder.Count];
        int na = 0, nb = 0, lmgAt = -1;
        for (var i = 0; i < els.Length; i++)
        {
            els[i] = (byte)(i % 2 == 0 ? 1 : 2);
            if (lmgAt < 0 && Unit(_lineOrder[i].Id) is { Fight.Lmg: true })
            {
                lmgAt = i;
            }
        }
        if (lmgAt >= 0 && els[lmgAt] == 1)
        {
            els[lmgAt] = 2;
            foreach (var e in els)
            {
                (na, nb) = e == 1 ? (na + 1, nb) : (na, nb + 1);
            }
            if (nb - na > 1)
            {
                var best = -1;
                for (var i = 0; i < els.Length; i++)
                {
                    if (i != lmgAt && els[i] == 2 && (best < 0 || Math.Abs(i - lmgAt) < Math.Abs(best - lmgAt)))
                    {
                        best = i;
                    }
                }
                if (best >= 0)
                {
                    els[best] = 1;
                }
            }
        }
        for (var i = 0; i < m.LineList.Count; i++)
        {
            var p = m.LineList[i];
            var at = _lineOrder.FindIndex(x => x.Id == p.Unit);
            m.LineList[i] = p with { Element = at >= 0 ? els[at] : (byte)0 };
        }
    }

    // AdvanceDir: toward the squad's goal under an order, toward the enemy group otherwise (11.3 step 2).
    private int AdvanceDir(Squad s, SquadMind m, Cell centre)
    {
        var to = s.Order == OrderKind.AttackMove && s.Goal != centre ? s.Goal : m.GroupAt;
        return to == centre ? m.LineAxis : Dir32(to.X - centre.X, to.Y - centre.Y);
    }

    // Advance: marching fire when it applies (11.4), else bounding overwatch (11.3).
    private void Advance(Squad s, SquadMind m, Cell centre, InfantryCombatRules r)
    {
        var hit = false;
        var firedOn = 0u;
        foreach (var p in m.LineList)
        {
            if (Unit(p.Unit) is { Fight: { } f } u && Able(u))
            {
                hit |= m.Marching && u.LastHitTick != 0 && u.LastHitTick >= m.MarchSince;
                firedOn = Math.Max(firedOn, Math.Max(f.SuppressedAt, u.LastHitTick));
            }
        }
        var pinnedHalf = m.GroupPinned * 2 >= m.GroupCount;
        var quiet = firedOn == 0 || Tick - firedOn >= (uint)r[CombatValue.SqMarchQuietTicks];
        if (m.Marching && (hit || (!quiet && !pinnedHalf)))
        {
            EndBound(s, m, false); // a member hit, or the enemy firing in strength: back to bounds
        }
        else if (!m.Marching && m.Moving == 0 && m.Ratio >= r[CombatValue.SqMarchR] && (pinnedHalf || quiet))
        {
            (m.Marching, m.MarchSince) = (true, Tick);
        }
        var dir = AdvanceDir(s, m, centre);
        if (m.Marching)
        {
            March(m, dir, r);
            return;
        }
        BoundControl(s, m, dir, r);
    }

    // March (11.4): every able member's goal marchStep ahead of where he is, the line kept (each from his own place).
    private void March(SquadMind m, int dir, InfantryCombatRules r)
    {
        var (dx, dy) = InfantryCombatDirections(dir);
        var step = (long)r[CombatValue.SqMarchStep];
        for (var i = 0; i < m.LineList.Count; i++)
        {
            var p = m.LineList[i];
            if (Unit(p.Unit) is not { } u || !Able(u) || u.Stance is not (Stance.Guard or Stance.Aggressive))
            {
                continue;
            }
            var to = new Vec(Fixed.FromRaw(u.Pos.X.Raw + dx * step * Fixed.OneRaw / 10240), Fixed.FromRaw(u.Pos.Y.Raw + dy * step * Fixed.OneRaw / 10240));
            var c = Cell.Of(OnMap(to));
            m.LineList[i] = p with { Slot = Standable(c) ? c : p.Slot, Goal = Standable(c) ? c : Cell.Of(u.Pos), Bounding = true };
        }
    }

    // EndBound: the bound (or the march) ends; with finish the movers keep walking to their goals (a choice changed in mid-bound),
    // else they stop and drop where they are; their slot is where they end.
    private void EndBound(Squad s, SquadMind m, bool finish)
    {
        for (var i = 0; i < m.LineList.Count; i++)
        {
            var p = m.LineList[i];
            if (!p.Bounding)
            {
                continue;
            }
            var here = Unit(p.Unit) is { } u ? Cell.Of(u.Pos) : p.Goal;
            m.LineList[i] = finish ? p with { Slot = p.Goal, Bounding = false } : p with { Slot = here, Goal = here, Bounding = false };
        }
        if (m.Moving != 0)
        {
            (m.Last, m.Moving, m.SettledTick) = (m.Moving, 0, Tick);
        }
        m.Marching = false;
        _ = s;
    }

    // BoundControl (11.3's pseudo-code): never two elements moving; the next starts only once the last has settled (or boundMaxTicks),
    // A first; its base of fire ready (stationary and firing or aiming in readyTicks) or readyTicks after the last settled.
    private void BoundControl(Squad s, SquadMind m, int dir, InfantryCombatRules r)
    {
        if (m.Moving != 0)
        {
            var settled = true;
            foreach (var p in m.LineList)
            {
                if (p.Bounding && Unit(p.Unit) is { } u && Able(u))
                {
                    settled &= Cell.Of(u.Pos) == p.Goal && !u.Moving;
                }
            }
            if (settled || Tick - m.BoundTick >= (uint)r[CombatValue.SqBoundMaxTicks])
            {
                EndBound(s, m, false); // nobody is still running when the next bound starts
            }
            return;
        }
        if (m.Last == 0 && !m.LineList.Exists(p => p.Element != 0))
        {
            DealElements(s, m); // dealt when the Advance starts, kept through it
        }
        var next = (byte)(m.Last == 1 ? 2 : 1);
        var other = (byte)(3 - next);
        if (!Ready(m, other, r) && Tick - m.SettledTick < (uint)r[CombatValue.SqReadyTicks])
        {
            return;
        }
        var (dx, dy) = InfantryCombatDirections(dir);
        long Along(Cell c) => (c.X * 10L * dx + c.Y * 10L * dy) / 1024; // tenths of a cell along the advance
        long Across(Cell c) => (c.X * 10L * -dy + c.Y * 10L * dx) / 1024;
        long otherLine = 0, n = 0;
        foreach (var p in m.LineList)
        {
            if (p.Element == other && Unit(p.Unit) is { } u && Able(u))
            {
                (otherLine, n) = (otherLine + Along(Cell.Of(u.Pos)), n + 1);
            }
        }
        if (n == 0)
        {
            foreach (var p in m.LineList)
            {
                if (p.Element == next && Unit(p.Unit) is { } u && Able(u))
                {
                    (otherLine, n) = (otherLine + Along(Cell.Of(u.Pos)), n + 1);
                }
            }
        }
        if (n == 0)
        {
            return; // no able element left: dealt again at the next Advance
        }
        otherLine /= n;
        var leap = m.Ratio >= r[CombatValue.SqLeapR];
        var (lo, hi) = leap ? (r[CombatValue.SqLeapMin], r[CombatValue.SqLeapMax]) : next == 1 ? (r[CombatValue.SqBoundMin], r[CombatValue.SqBoundMax]) : (0, 10);
        _lineTaken.Clear();
        foreach (var p in m.LineList)
        {
            _lineTaken.Add(p.Goal);
        }
        long progress = 0, movers = 0;
        for (var i = 0; i < m.LineList.Count; i++)
        {
            var p = m.LineList[i];
            if (p.Element != next || Unit(p.Unit) is not { } u || !Able(u) || u.Stance is not (Stance.Guard or Stance.Aggressive))
            {
                continue;
            }
            var goal = BoundGoal(u, p, dir, otherLine + lo, otherLine + hi, Across(p.Slot), Along, Across, r);
            if (goal is not { } g)
            {
                continue; // no walkable cell in his box: he stays with the base of fire this time
            }
            _lineTaken.Add(g);
            progress += Along(g) - Along(Cell.Of(u.Pos));
            movers++;
            m.LineList[i] = p with { Goal = g, Bounding = true };
        }
        if (movers == 0)
        {
            (m.Last, m.SettledTick) = (next, Tick);
            return;
        }
        if (next == 2)
        {
            // The rear roles move with the second bound of each pair, as far along as B goes on average.
            var step = progress / movers;
            for (var i = 0; i < m.LineList.Count; i++)
            {
                var p = m.LineList[i];
                if (p.Element != 0 || Unit(p.Unit) is not { } u || !Able(u) || u.Stance is not (Stance.Guard or Stance.Aggressive) || step <= 0)
                {
                    continue;
                }
                var to = Cell.Of(OnMap(new Vec(Fixed.FromRaw(u.Pos.X.Raw + dx * step * Fixed.OneRaw / 10240), Fixed.FromRaw(u.Pos.Y.Raw + dy * step * Fixed.OneRaw / 10240))));
                if (Standable(to) && !_lineTaken.Contains(to))
                {
                    _lineTaken.Add(to);
                    m.LineList[i] = p with { Goal = to, Bounding = true };
                }
            }
        }
        (m.Moving, m.BoundTick) = (next, Tick);
        TacticsStats.Bounds++;
    }

    // Ready (11.3 step 1): an able member of element e stands still and fired or aimed at a contact in the last readyTicks.
    private bool Ready(SquadMind m, byte e, InfantryCombatRules r)
    {
        foreach (var p in m.LineList)
        {
            if (p.Element == e && Unit(p.Unit) is { } u && Able(u) && !u.Moving &&
                (u.AutoTarget != 0 || (u.LastShot != 0 && Tick - u.LastShot <= (uint)r[CombatValue.SqReadyTicks])))
            {
                return true;
            }
        }
        return false;
    }

    // BoundGoal (11.3 step 2): in his goal box (lo to hi tenths along the advance, his slot's track +- boundAcross), the cell with cover
    // toward the axis and the least exposure kneeling (ties: the box's order); with no cover there, the box's nearest standable
    // cell on his track; null when the box has none.
    private Cell? BoundGoal(Unit u, LinePlace p, int dir, long lo, long hi, long track, Func<Cell, long> along, Func<Cell, long> across, InfantryCombatRules r)
    {
        var (dx, dy) = InfantryCombatDirections(dir);
        Cell? best = null, open = null;
        long bestE = long.MaxValue, openD = long.MaxValue;
        var axis = u.Fight is { Axis: >= 0 } f ? f.Axis : dir;
        var seen = 0;
        for (var a = lo; a <= hi; a += 10)
        {
            for (var x = -r[CombatValue.SqBoundAcross]; x <= r[CombatValue.SqBoundAcross]; x += 10)
            {
                var t = track + x;
                // the cell at (along a, across t), back from the advance frame (dx, dy) x 1024, in tenths
                var c = new Cell((int)((a * dx - t * dy) / 10240), (int)((a * dy + t * dx) / 10240));
                if (!Standable(c) || _lineTaken.Contains(c) || ++seen > r[CombatValue.PosMaxCandidates])
                {
                    continue;
                }
                if (InCoverToward(c, axis, r))
                {
                    var e = Exposure(u, c, Posture.Kneel);
                    if (e < bestE)
                    {
                        (best, bestE) = (c, e);
                    }
                }
                var d = Math.Abs(across(c) - track) * 1000 + Math.Abs(along(c) - lo);
                if (d < openD)
                {
                    (open, openD) = (c, d);
                }
            }
        }
        _ = p;
        return best ?? open;
    }

    // ---- 11.6: suppressive fire ----

    // OrderSuppression (11.6): who fires at a place. In Fight in place (or as the base of fire while the other element bounds), a
    // member with no aimed target and the enemy group within his range, or whose target is in hard cover with his hit chance under
    // suppressHitBelow; never in Hold fire or Hide, under suppressAmmoPercent of his load (the LMG lmgSuppressAmmo), with a friend
    // within suppressAimClear of the aim, or once the group shows no sign for suppressLiftTicks. The LMG suppresses the group.
    private void OrderSuppression(Squad s, SquadMind m, InfantryCombatRules r)
    {
        var lift = m.GroupCount == 0 || m.GroupSeen == 0 || Tick - m.GroupSeen >= (uint)r[CombatValue.SqSuppressLiftTicks];
        var clear = (long)r[CombatValue.SqSuppressAimClear] * Fixed.OneRaw / 10;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is not { Fight: { } f } u)
            {
                continue;
            }
            var want = false;
            var at = m.GroupAt;
            if (!lift && Able(u) && m.Choice is SquadChoice.FightInPlace or SquadChoice.Advance && u.Stance is not (Stance.HoldFire or Stance.Hide) &&
                u.Def.Weapons.Count > 0 && (m.Choice == SquadChoice.FightInPlace || (m.Moving != 0 && LineIndex(m, id) is >= 0 and var li && !m.LineList[li].Bounding)))
            {
                var w = u.Def.Weapons[u.Arms is { } a ? a.InHand : 0];
                var ammo = w.Stock < 0 || w.Effect.Carried <= 0 ||
                    (long)u.Charges[w.Stock] * 100 >= (long)w.Effect.Carried * (f.Lmg ? r[CombatValue.SqLmgSuppressAmmo] : r[CombatValue.SqSuppressAmmoPercent]);
                if (w.Effect.Accuracy > 0 && ammo)
                {
                    if (Unit(u.AutoTarget) is { Alive: true } t)
                    {
                        var tc = Cell.Of(t.Pos);
                        if (HitChance(u, t, w) < r[CombatValue.SqSuppressHitBelow] && t.Fight is not null &&
                            CoverToward(tc, Dir32(u.Pos.X.Raw - t.Pos.X.Raw, u.Pos.Y.Raw - t.Pos.Y.Raw), r) is CoverKind.Low or CoverKind.High)
                        {
                            (want, at) = (true, tc);
                        }
                    }
                    else
                    {
                        want = (u.Pos - m.GroupAt.Center()).LenSq() <= w.Range.Raw * w.Range.Raw;
                    }
                }
                if (want)
                {
                    foreach (var mid in s.MemberList)
                    {
                        want &= mid == id || Unit(mid) is not { Alive: true } mate || (mate.Pos - at.Center()).LenSq() > clear * clear;
                    }
                }
            }
            (f.Suppressing, f.SuppressAt) = (want, want ? at : default);
        }
    }

    // FireSuppressive (11.6): a shot of u's arm in hand at his squad's area: no hit roll unless an enemy soldier stands within
    // suppressHitNear of the aim (then a suppressHitPercent share of the hit chance); it lands within suppressSpread of the aim
    // (seeded) and suppresses those within nearShot of where it lands. The weapon's own bursts and waits (the LMG's lmgWait);
    // held while a squadmate is within fireLineClear of the line and nearer. True when he was busy with it (no aimed fire either).
    private bool FireSuppressive(Unit u, SoldierFight f, List<Hit> hits)
    {
        if (Combat is not { } r)
        {
            return false;
        }
        var wi = u.Arms is { } a ? a.InHand : 0;
        var w = u.Def.Weapons[wi];
        var aim = f.SuppressAt.Center();
        if (w.Effect.Accuracy <= 0 || (aim - u.Pos).LenSq() > w.Range.Raw * w.Range.Raw)
        {
            return false;
        }
        if (u.Moving && !FiresOnTheMove(u))
        {
            return true;
        }
        Facing.TryFromDirection(aim - u.Pos, out var want);
        var faced = AimHull(u, want);
        if (u.Reload > 0 || Math.Abs(Facing.Diff(faced, want)) > FireAlignFacings || f.PostureLeft > 0 || f.DeployLeft > 0 || ThinkHolds(u, f, Tick) ||
            !MagazineReady(u, w) || (w.Stock >= 0 && u.Charges[w.Stock] <= 0) || MateInLine(u, aim, r))
        {
            return true;
        }
        if (w.Stock >= 0 && !w.Effect.AmmoPerRocket)
        {
            u.MutableCharges[w.Stock]--;
        }
        SpendMagazineRound(u, w);
        u.Reload = w.Reload * 100;
        u.LastShot = Tick;
        TacticsStats.Suppressive++;
        var spread = r[CombatValue.SqSuppressSpread];
        var hx = Rand.Hash(Match.Setup.Seed, Tick, u.Id, SuppressSalt);
        var ox = (long)(hx % (ulong)(2 * spread + 1)) - spread;
        var oy = (long)((hx >> 20) % (ulong)(2 * spread + 1)) - spread;
        var at = new Vec(Fixed.FromRaw(aim.X.Raw + ox * Fixed.OneRaw / 10), Fixed.FromRaw(aim.Y.Raw + oy * Fixed.OneRaw / 10));
        var near = (long)r[CombatValue.SqSuppressHitNear] * Fixed.OneRaw / 10;
        Unit? man = null;
        foreach (var e in Around(aim.X.Raw - near, aim.Y.Raw - near, aim.X.Raw + near, aim.Y.Raw + near, _nearShot, true))
        {
            if (e.Alive && e.Owner != u.Owner && e.Owner != 0 && e.Life is not null && (e.Pos - aim).LenSq() <= near * near && (man is null || e.Id < man.Id))
            {
                man = e;
            }
        }
        var hit = man is not null && (long)(Rand.Hash(Match.Setup.Seed, Tick, u.Id, man.Id | SuppressHitSalt) % 1000) <
            HitChance(u, man, w) * r[CombatValue.SqSuppressHitPercent] / 100;
        if (hit)
        {
            hits.Add(new Hit(w, man!.Id, man.Pos, u.Id));
        }
        _shotEvents.Add(new ShotEvent(u.Id, hit ? man!.Id : 0, hit, hit ? man!.Pos : at, wi));
        NoteAreaShot(u, w, hit ? man : null, hit ? man!.Pos : at, r);
        if (f.Lmg && f.BurstLeft <= 0 && f.FireAt > Tick + (uint)r[CombatValue.SqLmgWait])
        {
            f.FireAt = Tick + (uint)r[CombatValue.SqLmgWait]; // the LMG fires its bursts with the shortest wait (11.6)
        }
        return true;
    }

    // NoteAreaShot: a suppressive shot's burst count and the suppression round where it lands (brief 6.6's near misses).
    private void NoteAreaShot(Unit u, WeaponDef w, Unit? hitMan, Vec at, InfantryCombatRules r)
    {
        NoteBurst(u, w, r);
        if (u.Fight is { Lmg: true, BurstLeft: <= 0 } lf && lf.FireAt > Tick + (uint)r[CombatValue.SqLmgWait])
        {
            lf.FireAt = Tick + (uint)r[CombatValue.SqLmgWait];
        }
        OpenFire(u);
        var sup = r.Weapon(w.Effect)?.Suppress ?? 0;
        var near = r[CombatValue.NearShot] * Fixed.OneRaw / 10;
        var range = Math.Max(1L, w.Range.Raw);
        foreach (var s in Around(at.X.Raw - near, at.Y.Raw - near, at.X.Raw + near, at.Y.Raw + near, _nearShot, true))
        {
            if (s.Fight is null || !s.Alive || s.Owner == u.Owner || s.Owner == 0 || s.Inside != 0 || s.Garrison != 0)
            {
                continue;
            }
            var d = s.Pos - at;
            if (d.X.Raw * d.X.Raw + d.Y.Raw * d.Y.Raw <= near * near)
            {
                Suppress(s, sup + (s == hitMan ? r[CombatValue.SuppressHit] : 0), u.Pos, r);
                ShotNear(s, u, range);
                OpenFire(s);
            }
        }
    }

    // MateInLine (15.7, 11.6): a squadmate within fireLineClear of the line from u to `to` and nearer than it.
    private bool MateInLine(Unit u, Vec to, InfantryCombatRules r)
    {
        if (u.Squad == 0 || Squad(u.Squad) is not { } s)
        {
            return false;
        }
        var clear = (long)r[CombatValue.PosFireLineClear] * Fixed.OneRaw / 10;
        var far = (to - u.Pos).LenSq();
        foreach (var id in s.MemberList)
        {
            if (id != u.Id && Unit(id) is { Alive: true, Inside: 0, Garrison: 0 } m && (m.Pos - u.Pos).LenSq() < far && NearLine(m.Pos, u.Pos, to, clear))
            {
                return true;
            }
        }
        return false;
    }

    // OpenFire (15.7): the first shot by or at a member sets his squad's "opened fire" flag.
    private void OpenFire(Unit u)
    {
        if (u.Squad != 0 && Squad(u.Squad) is { Mind: { OpenedFire: false } m })
        {
            m.OpenedFire = true;
        }
    }

    // HoldsFireTactics (15.7, 16.2): concealed and unseen by the target's side, a soldier opens fire only when his squad has, unless he
    // leads it or his hit chance is openFireChance or more; never through a squadmate; an LMG setting up, or deployed and his
    // target outside his arc.
    private bool HoldsFireTactics(Unit u, SoldierFight f, Unit target, InfantryCombatRules r)
    {
        if (f.DeployLeft > 0)
        {
            return true;
        }
        if (f.Deployed && f.DeployDir >= 0 && DirSteps(Dir32(target.Pos.X.Raw - u.Pos.X.Raw, target.Pos.Y.Raw - u.Pos.Y.Raw), f.DeployDir) > r[CombatValue.SqLmgArcSteps])
        {
            return true;
        }
        if (u.Squad != 0 && Squad(u.Squad) is { Mind: { OpenedFire: false } } s && s.Leader != u.Id && (u.SpottedBy & Bit(target.Owner)) == 0 &&
            _cover is { } cm && Grid.Contains(Cell.Of(u.Pos)) && cm.Density(Cell.Of(u.Pos).Y * cm.W + Cell.Of(u.Pos).X) >= r[CombatValue.ConcealDensity] &&
            u.Def.Weapons.Count > 0 && HitChance(u, target, u.Def.Weapons[u.Arms is { } a ? a.InHand : 0]) < r[CombatValue.TargetOpenFireChance])
        {
            return true;
        }
        return MateInLine(u, target.Pos, r);
    }

    // ---- 15.6: reloading as a squad ----

    // ReloadWaits (15.6): a third or more of the squad's shooters (rounded up) reloading, a man with keepFiring% or more of his
    // magazine keeps firing (the lower IDs reloaded first: RunArms goes in ID order).
    internal bool ReloadWaits(Unit u, int mag, int size)
    {
        if (Combat is not { } r || u.Fight is null || u.Squad == 0 || Squad(u.Squad) is not { } s || (long)mag * 100 < (long)size * r[CombatValue.TargetKeepFiring])
        {
            return false;
        }
        int shooters = 0, reloading = 0;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { } m && Able(m) && m.Def.Weapons.Count > 0)
            {
                shooters++;
                reloading += m.Arms is { Reloading: >= 0 } ? 1 : 0;
            }
        }
        var share = Math.Max(1, r[CombatValue.TargetReloadShare]);
        return reloading > 0 && reloading * share >= shooters;
    }

    // TacticalReload (15.6): ducked in cover, or no target for noTargetTicks, with the magazine at tacticalReload% or less.
    internal bool TacticalReload(Unit u, int mag, int size) =>
        Combat is { } r && u.Fight is { } f && mag > 0 && (long)mag * 100 <= (long)size * r[CombatValue.TargetTacticalReload] &&
        (f.Ducked || (u.AutoTarget == 0 && f.NoTargetSince != 0 && Tick - f.NoTargetSince >= (uint)r[CombatValue.TargetNoTargetTicks]));

    // ---- 16.2: the LMG's deploy ----

    // TickLmg (16.2): on a stop longer than lmgStopTicks, alert or in contact, he sets up in lmgDeployTicks (no fire): kneeling over low
    // cover facing his threat, else prone; moving ends it. Deployed, a target or danger contact outside his arc for lmgOutTicks turns
    // him to it (lmgRedeployTicks).
    private void TickLmg(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        if (!u.Alive || Walking(u) || u.Inside != 0 || u.Garrison != 0 || u.Life is { Wound: WoundState.Down } or { Carrier: not 0 })
        {
            if (f.Deployed || f.DeployLeft > 0)
            {
                (f.Deployed, f.DeployLeft, f.DeployDir, f.Forced) = (false, 0, -1, null);
            }
            (f.StillSince, f.ArcOutSince) = (0, 0);
            return;
        }
        if (f.StillSince == 0)
        {
            f.StillSince = Tick;
        }
        if (f.DeployLeft > 0)
        {
            if (--f.DeployLeft == 0)
            {
                f.Deployed = true;
            }
            return;
        }
        var face = u.AutoTarget != 0 && Unit(u.AutoTarget) is { } t ? Dir32(t.Pos.X.Raw - u.Pos.X.Raw, t.Pos.Y.Raw - u.Pos.Y.Raw) : f.Axis;
        if (!f.Deployed)
        {
            if ((f.InContact || f.Alert) && face >= 0 && Tick - f.StillSince >= (uint)r[CombatValue.SqLmgStopTicks])
            {
                (f.DeployLeft, f.DeployDir) = (r[CombatValue.SqLmgDeployTicks], face);
                f.Forced = CoverToward(Cell.Of(u.Pos), face, r) == CoverKind.Low ? Posture.Kneel : ProneAllowed(u, Cell.Of(u.Pos)) ? Posture.Prone : Posture.Kneel;
            }
            return;
        }
        if (face >= 0 && f.DeployDir >= 0 && DirSteps(face, f.DeployDir) > r[CombatValue.SqLmgArcSteps])
        {
            if (f.ArcOutSince == 0)
            {
                f.ArcOutSince = Tick;
            }
            else if (Tick - f.ArcOutSince >= (uint)r[CombatValue.SqLmgOutTicks])
            {
                (f.DeployLeft, f.DeployDir, f.ArcOutSince) = (r[CombatValue.SqLmgRedeployTicks], face, 0);
                f.Deployed = false;
            }
        }
        else
        {
            f.ArcOutSince = 0;
        }
    }
}
