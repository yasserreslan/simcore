using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// Soldiers (INF-E2a; docs/design.md, "Wounds, rescue, bodies, ranks and Retreat"; the game repo's infantry-combat-brief 6.5, 7.1,
// 7.2, 7.5, 7.6), for every unit the content's soldiers block applies to (Unit.Life):
// - Wounds: wounded at or under wounds.wounded of max HP (slower, worse aim); down under wounds.down and above 0, on foot on the map
//   (can't act: CanAct false, no orders, no fire, out of the crowd), crawling to a squadmate or hard cover within crawlReach, bleeding
//   out in bleedTicks. Purely by HP: a medic's treatment lifts him to treatHp, back to wounded. Death at 0 or less, as before.
// - Rescue: the nearest free squadmate (not a medic, safe: no enemy shot at him for calmTicks, no enemy his side sees within
//   enemyRange% of its range of the downed man) walks to him, carries him at speed% without firing to the nearest hard cover within
//   rescue.cover (or toward the squad's medic; on a retreat, to the retreat's spot), and puts him down: the bleeding stops pauseTicks.
// - The medic (no order of his player's): the downed within medic.search first (8 s kneeling: back to wounded), then the wounded.
// - Bodies: a soldier who dies on the map leaves a Body with his leftover carried ammo by group (bodies.ticks, at most bodies.max).
//   Scavenge: ordered (CommandKind.Scavenge) or on his own (low on ammo, safe, a useful body within reach): kneels, takes what fits.
// - Ranks: points for kills, shares, battles survived, rescues and treatments; aim and magazine-reload bonuses (suppression: a
//   hook); a squad's leader is its highest rank (SquadSystem.Rebuild).
// - Retreat (CommandKind.Retreat): away from the known threat (or to a dragged point), bounding in pairs, then Hold.
// RunCare runs after the squads (orders and tasks, so the paths system routes them this tick); RunWounds after the tick's damage
// and healing, before the cleanup (states, crawling, carried men, bleeding, bodies, battles, ranks). Units in ID order, integers.
public sealed partial class World
{
    private readonly List<Body> _bodies = []; // oldest first
    private readonly List<SoldierEvent> _soldierEvents = [];
    private readonly List<Unit> _careAble = []; // scratch, not state
    private bool _soldiers; // a unit with a Life was ever added (worlds without them skip the systems)

    /// <summary>The bodies on the map, oldest first (INF-E2a).</summary>
    public IReadOnlyList<Body> Bodies => _bodies;

    /// <summary>
    /// The soldier events of the last tick, in order: wounded, down, bled out, treated, carry start and stop, scavenged, rank up,
    /// retreat start and end, healed, bodies lying and gone, treatment and scavenging started.
    /// </summary>
    public IReadOnlyList<SoldierEvent> SoldierEvents => _soldierEvents;

    /// <summary>The body with this ID (the dead soldier's unit ID), or null.</summary>
    public Body? BodyOf(uint id)
    {
        foreach (var b in _bodies)
        {
            if (b.Id == id)
            {
                return b;
            }
        }
        return null;
    }

    // DownNow: u is down by his HP now (before this tick's RunWounds has classified him): healing auras skip him.
    private bool DownNow(Unit u) =>
        u.Life is not null && Content.Soldiers is { } r && u.Alive && u.Inside == 0 && u.Garrison == 0 &&
        (long)u.HP * 100 < (long)u.Def.MaxHp * r[SoldierValue.DownPercent];

    /// <summary>Whether the unit is down (INF-E2a): it can't act, crawls and bleeds.</summary>
    public static bool IsDown(Unit u) => u.Life is { Wound: WoundState.Down };

    private int SV(SoldierValue v) => Content.Soldiers![v];

    /// <summary>His aim, percent of his weapon's (rank bonus, wound penalty): 100 for units without a rank.</summary>
    public int AimPercent(Unit u)
    {
        if (u.Life is not { } l || Content.Soldiers is not { } r)
        {
            return 100;
        }
        var rank = l.Rank switch
        {
            SoldierRank.Regular => r[SoldierValue.AimRegular],
            SoldierRank.Veteran => r[SoldierValue.AimVeteran],
            SoldierRank.Elite => r[SoldierValue.AimElite],
            _ => 0,
        };
        var pct = 100 + rank;
        return l.Wound == WoundState.Healthy ? pct : pct * (100 + r[SoldierValue.WoundedAim]) / 100;
    }

    /// <summary>His walking speed, percent of his own: a wounded man's penalty, a carrier's pace (100 otherwise).</summary>
    public int WalkPercent(Unit u)
    {
        if (u.Life is not { } l || Content.Soldiers is not { } r)
        {
            return 100;
        }
        var pct = l.Wound == WoundState.Wounded ? 100 + r[SoldierValue.WoundedSpeed] : 100;
        if (l.Carrying != 0 && Unit(l.Carrying) is { Life.Carrier: var c } && c == u.Id)
        {
            pct = pct * r[SoldierValue.RescueSpeed] / 100;
        }
        return Math.Max(1, pct);
    }

    /// <summary>His magazine reloads, percent of the weapon's time (rank bonus; also posture changes once INF-E1b has them).</summary>
    public int ReloadPercent(Unit u)
    {
        if (u.Life is not { } l || Content.Soldiers is not { } r)
        {
            return 100;
        }
        return 100 - l.Rank switch
        {
            SoldierRank.Regular => r[SoldierValue.ReloadRegular],
            SoldierRank.Veteran => r[SoldierValue.ReloadVeteran],
            SoldierRank.Elite => r[SoldierValue.ReloadElite],
            _ => 0,
        };
    }

    /// <summary>
    /// Hook for INF-E1b: his posture changes, percent of the normal speed (a wounded man's penalty, a rank's bonus). Nothing uses it
    /// yet: the engine has no postures.
    /// </summary>
    public int PostureChangePercent(Unit u)
    {
        if (u.Life is not { } l || Content.Soldiers is not { } r)
        {
            return 100;
        }
        var pct = 10000 / Math.Max(1, ReloadPercent(u)); // faster changes: a shorter time
        return l.Wound == WoundState.Healthy ? pct : pct * (100 + r[SoldierValue.WoundedPosture]) / 100;
    }

    /// <summary>
    /// Hook for INF-E1b: his suppression gains, percent of the normal (a wounded man's more, a rank's less). Nothing uses it yet: the
    /// engine has no suppression.
    /// </summary>
    public int SuppressionGainPercent(Unit u)
    {
        if (u.Life is not { } l || Content.Soldiers is not { } r)
        {
            return 100;
        }
        var pct = 100 - l.Rank switch
        {
            SoldierRank.Regular => r[SoldierValue.SuppressRegular],
            SoldierRank.Veteran => r[SoldierValue.SuppressVeteran],
            SoldierRank.Elite => r[SoldierValue.SuppressElite],
            _ => 0,
        };
        return l.Wound == WoundState.Healthy ? pct : pct * (100 + r[SoldierValue.WoundedSuppression]) / 100;
    }

    // Safe is the suppression hook (INF-E1b replaces it with "suppression under 30"): no enemy fired at him or hurt him in the last
    // calm ticks.
    private bool Safe(Unit u, int calm) =>
        u.Fight is { } f && Combat is { } ic ? f.Suppression < ic[CombatValue.Shaken] && (u.LastHitTick == 0 || Tick - u.LastHitTick >= (uint)calm) : // INF-E1b-1
        (u.Life is not { ShotAt: var s } || s == 0 || Tick - s >= (uint)calm) && (u.LastHitTick == 0 || Tick - u.LastHitTick >= (uint)calm);

    // NewLife gives a new unit its Life when the soldiers block applies to it (World.Add).
    private void NewLife(Unit u)
    {
        if (Content.Soldiers is { } r && r.Applies(u.Def.Tags) && !u.Def.IsStructure)
        {
            u.Life = new SoldierLife();
            _soldiers = true;
        }
    }

    // NoteShotAt: shooter u fired at t this tick (hit or miss): both are in a battle; t was shot at.
    internal void NoteShotAt(Unit u, Unit t)
    {
        if (u.Life is { } ul)
        {
            (ul.InBattle, ul.BattleShot) = (true, Tick);
        }
        if (t.Life is { } tl && t.Owner != u.Owner)
        {
            (tl.ShotAt, tl.InBattle, tl.BattleShot) = (Tick, true, Tick);
        }
    }

    // NoteHurt: enemy `by` (or null) took t from hp to t.HP (Hurt). A soldier keeps who hurt him lately (kill shares); the killing
    // hit gives its shooter the kill's points.
    internal void NoteHurt(Unit t, Unit? by, int hp)
    {
        if (by is null || hp <= 0 || t.HP >= hp || Content.Soldiers is not { } r)
        {
            return;
        }
        if (t.Life is { } tl)
        {
            (tl.InBattle, tl.BattleShot) = (true, Tick);
            var d = tl.Damagers;
            d.RemoveAll(x => x.Unit == by.Id || Tick - x.Tick > (uint)r[SoldierValue.XpShareTicks]);
            var at = 0;
            while (at < d.Count && d[at].Unit < by.Id)
            {
                at++;
            }
            d.Insert(at, (by.Id, Tick));
            if (d.Count > 8)
            {
                var oldest = 0;
                for (var i = 1; i < d.Count; i++)
                {
                    oldest = d[i].Tick < d[oldest].Tick ? i : oldest;
                }
                d.RemoveAt(oldest);
            }
        }
        if (t.HP <= 0)
        {
            NoteMateKilled(t); // INF-E1b-1: suppression on his squadmates near him (InfantryCombatSystem.cs)
            Credit(t, by, r);
        }
    }

    // Credit gives killer the points for killing t (a soldier: by rank; else by the vehicle table), and every other enemy who hurt the
    // soldier within the share window a share.
    private void Credit(Unit t, Unit? killer, SoldierRules r)
    {
        if (killer is { Life: { } kl } && killer.Alive)
        {
            kl.Xp += t.Life is { } vl ? (vl.Rank > kl.Rank ? r[SoldierValue.XpKillHigher] : r[SoldierValue.XpKill])
                : t.Def.IsStructure ? 0 : r.VehiclePoints(t.Def.Tags);
        }
        if (t.Life is not { } tl)
        {
            return;
        }
        foreach (var (id, tick) in tl.Damagers)
        {
            if (id != (killer?.Id ?? 0) && Tick - tick <= (uint)r[SoldierValue.XpShareTicks] && Unit(id) is { Alive: true, Life: { } sl })
            {
                sl.Xp += r[SoldierValue.XpShare];
            }
        }
        tl.Damagers.Clear();
    }

    private void SoldierEvent(SoldierEventKind k, Unit u, uint other, int value) => _soldierEvents.Add(new SoldierEvent(k, u.Id, other, value, u.Pos));

    // ---- RunCare: tasks and orders (after the squads) ----

    private void RunCare()
    {
        if (!_soldiers || Content.Soldiers is not { } r)
        {
            return;
        }
        foreach (var s in _squads)
        {
            if (s.Retreating)
            {
                StepRetreat(s, r);
            }
        }
        foreach (var u in _units)
        {
            if (u.Life is not { } l || !u.Alive)
            {
                continue;
            }
            if (l.Carrying != 0)
            {
                StepCarry(u, l, r);
            }
            else if (l.Treating != 0)
            {
                StepTreat(u, l, r);
            }
            else if (l.Scavenging != 0)
            {
                StepScavenge(u, l, r);
            }
            if (l.Wound == WoundState.Down && l.Carrier == 0 && (Tick + u.Id) % 10 == 0)
            {
                PickRescuer(u, l, r);
            }
            var byWounded = l.Treating != 0 && l.TreatLeft < 0 && u.Order.Kind == OrderKind.Idle; // standing by a wounded man
            if ((Free(u) || byWounded) && (Tick + u.Id) % 10 == 5 && r.IsMedic(u.Def.Tags))
            {
                MedicLooks(u, l, r, downOnly: byWounded); // the downed first: one takes him off a wounded man
            }
            else if (Free(u) && (Tick + u.Id) % 20 == 0 && (u.Def.Tags.Bits & r.NoScavenge.Bits) == 0 && u.Arms is not null && LowBy(u, r[SoldierValue.ScavengeLow]) &&
                Safe(u, r[SoldierValue.ScavengeCalmTicks]))
            {
                if (NearestBody(u, Fixed.Frac(r[SoldierValue.ScavengeReach], 10)) is { } b)
                {
                    StartScavenge(u, l, b);
                }
            }
        }
    }

    // Free: u may take a task of his own: on foot on the map, able to act, no order of his player's (idle: in a squad, on his slot),
    // not fighting on his own, and no task already.
    private static bool Free(Unit u) =>
        u.Life is { Busy: false, Wound: not WoundState.Down } && u.Inside == 0 && u.Garrison == 0 && u.CanAct && u.Order.Kind == OrderKind.Idle &&
        u.Ai == AiState.None && u.Def.Speed.Raw > 0;

    // LowBy: his main arm carries ammo and has under pct% of its load left.
    private static bool LowBy(Unit u, int pct) =>
        u.Def.Weapons.Count > 0 && u.Def.Weapons[0] is { Stock: >= 0, Effect.Carried: > 0 } w && (long)u.Charges[w.Stock] * 100 < (long)u.StockMax(w.Stock) * pct;

    // Walk sends u on his task's walk to c (his TaskCell is where the walk really goes).
    private void Walk(Unit u, SoldierLife l, Cell c)
    {
        u.ResetAi();
        WalkTo(u, c, 0);
        l.TaskCell = u.Order.Cell;
    }

    // OnTask: u still walks his task's walk (no other order since).
    private static bool OnTask(Unit u, SoldierLife l) => u.Order.Kind == OrderKind.Move && u.Order.Cell == l.TaskCell;

    private static void Halt(Unit u)
    {
        u.Order = default;
        u.ClearPath();
    }

    // ---- Rescue ----

    // PickRescuer: for downed man d (not carried), the nearest free squadmate within rescue.reach, when it's safe, goes to carry him
    // to hard cover (or toward the medic; on a retreat, to its spot).
    private void PickRescuer(Unit d, SoldierLife dl, SoldierRules r)
    {
        if (d.Squad == 0 || Squad(d.Squad) is not { } s || !RescueSafe(d, r))
        {
            return;
        }
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Life.Carrying: var c } && c == d.Id)
            {
                return; // someone is on his way already
            }
        }
        Cell dest;
        var threat = ThreatOf(d);
        if (s.Retreating)
        {
            dest = s.RetreatSpot;
        }
        else if (HardCoverNear(d.Pos, threat, Fixed.Frac(r[SoldierValue.RescueCover], 10), r) is { } cover)
        {
            if (Within(cover.Center(), d.Pos, Fixed.Frac(r[SoldierValue.MedicReach], 10)))
            {
                return; // already in cover: he stays
            }
            dest = cover;
        }
        else if (SquadMedic(s, d) is { } medic)
        {
            if (Within(medic.Pos, d.Pos, Fixed.Frac(r[SoldierValue.MedicHealReach], 10)))
            {
                return; // he lies by the medic: he stays (else a carry each 10 ticks beside him, INF-C2b)
            }
            dest = Cell.Of(medic.Pos);
        }
        else
        {
            return;
        }
        Unit? best = null;
        var reach = Fixed.Frac(r[SoldierValue.RescueReach], 10);
        foreach (var id in s.MemberList)
        {
            var m = Unit(id)!;
            if (m == d || r.IsMedic(m.Def.Tags) || !(Free(m) || (s.Retreating && m.Life is { Busy: false, Wound: not WoundState.Down } && m.CanAct)) ||
                m.Inside != 0 || m.Garrison != 0 || !Within(m.Pos, d.Pos, reach) || !Safe(m, r[SoldierValue.RescueCalmTicks]))
            {
                continue;
            }
            if (best is null || (m.Pos - d.Pos).LenSq() < (best.Pos - d.Pos).LenSq())
            {
                best = m; // ties: the lower ID (member order)
            }
        }
        if (best is null)
        {
            return;
        }
        var bl = best.Life!;
        bl.Carrying = d.Id;
        bl.CarryTo = dest;
        if (!Within(best.Pos, d.Pos, Fixed.Frac(r[SoldierValue.MedicReach], 10)))
        {
            Walk(best, bl, Cell.Of(d.Pos)); // (beside him already: he picks him up next tick, one walk a tick)
        }
    }

    // RescueSafe: no enemy d's side sees stands within rescue.enemyRange percent of its first weapon's range of d.
    private bool RescueSafe(Unit d, SoldierRules r)
    {
        foreach (var e in _units)
        {
            if (e.Owner == d.Owner || e.Owner == 0 || !e.Alive || e.Def.Weapons.Count == 0 || IsDown(e) || !IsSpotted(e, d.Owner))
            {
                continue;
            }
            var reach = Fixed.FromRaw(e.Def.Weapons[0].Range.Raw * r[SoldierValue.RescueEnemyRange] / 100);
            if (Within(e.Pos, d.Pos, reach))
            {
                return false;
            }
        }
        return true;
    }

    // SquadMedic: the squad's medic able to treat (alive, on the map, not down), nearest d; null for none.
    private Unit? SquadMedic(Squad s, Unit d)
    {
        Unit? best = null;
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Inside: 0, Garrison: 0 } m && m.Alive && !IsDown(m) && Content.Soldiers!.IsMedic(m.Def.Tags) &&
                (best is null || (m.Pos - d.Pos).LenSq() < (best.Pos - d.Pos).LenSq()))
            {
                best = m;
            }
        }
        return best;
    }

    // ThreatOf: where u's danger comes from: where the last shot that hurt him came from, else 10 cells ahead of him.
    private static Vec ThreatOf(Unit u) => u.LastHitTick != 0 ? u.LastHitFrom : u.Pos + Facing.Dir(u.Facing).WithLen(Fixed.FromInt(10));

    // HardCoverNear: the nearest cell within reach of p that is hard cover from threat (Hold's cover points, at least rescue.hardCover),
    // standable; ties: Hold's offset order. Null without Hold's rules or with none.
    private Cell? HardCoverNear(Vec p, Vec threat, Fixed reach, SoldierRules r)
    {
        if (Combat is not null)
        {
            return HardCoverNearByData(p, threat, reach); // INF-E1b-1: the cover data (InfantryCombatSystem.cs)
        }
        if (Content.Squad?.Hold is not { } hold)
        {
            return null;
        }
        var at = Cell.Of(p);
        foreach (var (dx, dy) in HoldOffsets(reach.Raw))
        {
            var c = new Cell(at.X + dx, at.Y + dy);
            if (Grid.Contains(c) && Grid.Passable(c) && CoverPoints(c, threat, hold, Ground?.Forest(c) ?? 0) >= r[SoldierValue.HardCover])
            {
                return c;
            }
        }
        return null;
    }

    // StepCarry: walking to the downed man, picking him up, carrying him, putting him down.
    private void StepCarry(Unit u, SoldierLife l, SoldierRules r)
    {
        var d = Unit(l.Carrying);
        var picked = d?.Life is { } dl0 && dl0.Carrier == u.Id;
        if (d is not { Life: { } dl } || !d.Alive || dl.Wound != WoundState.Down || !u.CanAct || (!picked && dl.Carrier != 0))
        {
            EndCarry(u, l, d, false);
            return;
        }
        var beside = Fixed.Frac(r[SoldierValue.MedicReach], 10);
        if (!picked)
        {
            if (Within(u.Pos, d.Pos, beside))
            {
                dl.Carrier = u.Id;
                SoldierEvent(SoldierEventKind.CarryStart, u, d.Id, 0);
                Walk(u, l, l.CarryTo);
            }
            else if (u.Order.Kind == OrderKind.Idle && Within(u.Pos, d.Pos, Fixed.Frac(r[SoldierValue.RescueReach], 10)))
            {
                Walk(u, l, Cell.Of(d.Pos)); // stopped short (he crawls too): on to him
            }
            else if (!OnTask(u, l))
            {
                EndCarry(u, l, d, false);
            }
            return;
        }
        if (Within(u.Pos, l.CarryTo.Center(), beside) || u.Order.Kind == OrderKind.Idle || MedicBeside(d, beside))
        {
            dl.BleedPausedUntil = Tick + (uint)r[SoldierValue.RescuePauseTicks];
            l.Xp += r[SoldierValue.XpRescue];
            EndCarry(u, l, d, true);
            Halt(u);
        }
        else if (!OnTask(u, l))
        {
            EndCarry(u, l, d, false);
        }
    }

    // MedicBeside: d's squad medic treats him and is beside him (he met them on the way): the carry ends there (INF-C2b).
    private bool MedicBeside(Unit d, Fixed beside) =>
        d.Squad != 0 && Squad(d.Squad) is { } s && SquadMedic(s, d) is { Life.Treating: var t } m && t == d.Id && Within(m.Pos, d.Pos, beside);

    private void EndCarry(Unit u, SoldierLife l, Unit? d, bool atCover)
    {
        if (d?.Life is { } dl && dl.Carrier == u.Id)
        {
            dl.Carrier = 0;
            SoldierEvent(SoldierEventKind.CarryStop, u, d.Id, atCover ? 1 : 0);
        }
        l.Carrying = 0;
    }

    // ---- The medic ----

    // MedicLooks: a free medic goes to the nearest downed man of his side within medic.search that no other medic tends, else the
    // nearest wounded one.
    private void MedicLooks(Unit u, SoldierLife l, SoldierRules r, bool downOnly)
    {
        var search = Fixed.Frac(r[SoldierValue.MedicSearch], 10);
        Unit? down = null, hurt = null;
        foreach (var p in _units)
        {
            if (p == u || p.Owner != u.Owner || p.Life is not { } pl || !p.Alive || p.Inside != 0 || p.Garrison != 0 || pl.Wound == WoundState.Healthy ||
                !Within(u.Pos, p.Pos, search))
            {
                continue;
            }
            ref var best = ref pl.Wound == WoundState.Down ? ref down : ref hurt;
            if ((best is null || (p.Pos - u.Pos).LenSq() < (best.Pos - u.Pos).LenSq()) && !Tended(p, u))
            {
                best = p;
            }
        }
        if ((downOnly ? down : down ?? hurt) is not { } patient)
        {
            return;
        }
        if (u.Fight is { } mf && Combat is { } ic && mf.Suppression >= ic[CombatValue.Pinned] &&
            (patient.Life!.Wound != WoundState.Down || patient.Life.BleedLeft >= ic[CombatValue.MedicBleedTicks]))
        {
            return; // INF-E1b-1: under suppression 60 the medic waits in cover, unless his downed man has under medicBleedTicks of bleeding left
        }
        l.Treating = patient.Id;
        l.TreatLeft = 0;
        Walk(u, l, Cell.Of(patient.Pos));
    }

    // Tended: another medic tends p.
    private bool Tended(Unit p, Unit medic)
    {
        foreach (var o in _units)
        {
            if (o != medic && o.Life is { Treating: var t } && t == p.Id)
            {
                return true;
            }
        }
        return false;
    }

    // StepTreat: a medic walks to his patient; beside a downed man he kneels treatTicks, then the man is back to wounded at treatHp;
    // near a wounded one he stands by (his aid bag heals him) until he is healthy. A downed man elsewhere takes him off a wounded one.
    private void StepTreat(Unit u, SoldierLife l, SoldierRules r)
    {
        var p = Unit(l.Treating);
        if (p is not { Life: { } pl } || !p.Alive || pl.Wound == WoundState.Healthy || p.Inside != 0 || p.Garrison != 0 || !u.CanAct ||
            !Within(u.Pos, p.Pos, Fixed.Frac(r[SoldierValue.MedicSearch], 10)))
        {
            (l.Treating, l.TreatLeft) = (0, 0);
            return;
        }
        var down = pl.Wound == WoundState.Down;
        var reach = Fixed.Frac(down ? r[SoldierValue.MedicReach] : r[SoldierValue.MedicHealReach], 10);
        if (l.TreatLeft > 0)
        {
            if (!down || !Within(u.Pos, p.Pos, reach) || u.Order.Kind != OrderKind.Idle)
            {
                (l.Treating, l.TreatLeft) = (0, 0); // moved off, carried away, or another order
                return;
            }
            if (--l.TreatLeft == 0)
            {
                p.HP = Math.Max(p.HP, Math.Max(1, (int)((long)p.Def.MaxHp * r[SoldierValue.TreatHpPercent] / 100)));
                pl.Wound = WoundState.Wounded;
                (pl.BleedLeft, pl.BleedPausedUntil, pl.DownedBy) = (0, 0, 0);
                l.Xp += r[SoldierValue.XpTreat];
                l.Treating = 0;
                _soldierEvents.Add(new SoldierEvent(SoldierEventKind.Treated, p.Id, u.Id, p.HP, p.Pos));
            }
            return;
        }
        if (l.TreatLeft < 0)
        {
            if (down || !Within(u.Pos, p.Pos, reach) || u.Order.Kind != OrderKind.Idle)
            {
                (l.Treating, l.TreatLeft) = (0, 0); // another task (a downed man) or another order: look again
            }
            return;
        }
        if (Within(u.Pos, p.Pos, reach))
        {
            Halt(u);
            l.TreatLeft = down ? r[SoldierValue.TreatTicks] : -1;
            if (down)
            {
                SoldierEvent(SoldierEventKind.TreatStart, u, p.Id, 0);
            }
        }
        else if (!OnTask(u, l))
        {
            (l.Treating, l.TreatLeft) = (0, 0);
        }
        else if (!Within(l.TaskCell.Center(), p.Pos, Fixed.FromInt(2)))
        {
            Walk(u, l, Cell.Of(p.Pos)); // he was carried on: after him
        }
    }

    // ---- Bodies and scavenging ----

    // NearestBody: the nearest body within reach of u with something he can take; ties: the older body.
    private Body? NearestBody(Unit u, Fixed reach)
    {
        Body? best = null;
        foreach (var b in _bodies)
        {
            if (Within(u.Pos, b.Pos, reach) && Takes(u, b) > 0 && (best is null || (b.Pos - u.Pos).LenSq() < (best.Pos - u.Pos).LenSq()))
            {
                best = b;
            }
        }
        return best;
    }

    // Takes: how many rounds u could take from body b now (by ammo group, up to his full load); with take, he takes them.
    private static int Takes(Unit u, Body b, bool take = false)
    {
        var weapons = u.Def.Weapons;
        var total = 0;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if (w.Stock < 0 || w.Effect.Carried <= 0 || w.Effect.AmmoGroup == AmmoGroup.None || FirstOfStock(weapons, i))
            {
                continue;
            }
            var g = (int)w.Effect.AmmoGroup;
            var n = Math.Min(Math.Max(0, u.StockMax(w.Stock) - u.Charges[w.Stock]), b.AmmoBy[g]);
            if (take && n > 0)
            {
                u.MutableCharges[w.Stock] += n;
                b.AmmoBy[g] -= n;
            }
            total += n;
        }
        return total;
    }

    // FirstOfStock: an earlier weapon of the list draws on the same stock (it counts once).
    private static bool FirstOfStock(IReadOnlyList<WeaponDef> weapons, int i)
    {
        for (var j = 0; j < i; j++)
        {
            if (weapons[j].Stock == weapons[i].Stock)
            {
                return true;
            }
        }
        return false;
    }

    private void StartScavenge(Unit u, SoldierLife l, Body b)
    {
        l.Scavenging = b.Id;
        l.ScavengeLeft = 0;
        Walk(u, l, Cell.Of(b.Pos));
    }

    // StepScavenge: he walks to the body, kneels scavenge.ticks within scavenge.at of it, and takes what fits.
    private void StepScavenge(Unit u, SoldierLife l, SoldierRules r)
    {
        if (BodyOf(l.Scavenging) is not { } b || !u.CanAct)
        {
            (l.Scavenging, l.ScavengeLeft) = (0, 0);
            return;
        }
        if (l.ScavengeLeft > 0)
        {
            if (u.Order.Kind != OrderKind.Idle)
            {
                (l.Scavenging, l.ScavengeLeft) = (0, 0); // another order
            }
            else if (--l.ScavengeLeft == 0)
            {
                var n = Takes(u, b, take: true);
                SoldierEvent(SoldierEventKind.Scavenged, u, b.Id, n);
                l.Scavenging = 0;
            }
            return;
        }
        if (Within(u.Pos, b.Pos, Fixed.Frac(r[SoldierValue.ScavengeAt], 10)))
        {
            Halt(u);
            l.ScavengeLeft = Math.Max(1, r[SoldierValue.ScavengeTicks]);
            SoldierEvent(SoldierEventKind.ScavengeStart, u, b.Id, 0);
        }
        else if (!OnTask(u, l))
        {
            (l.Scavenging, l.ScavengeLeft) = (0, 0);
        }
    }

    // ScavengeCommand (CommandKind.Scavenge): each listed soldier of the player goes to body c.Target (none named: the nearest he can
    // use within scavenge.search) if it has something he can take, kneels and takes what fits.
    private void ScavengeCommand(Command c)
    {
        if (Content.Soldiers is not { } r)
        {
            return;
        }
        foreach (var id in c.Units)
        {
            if (Unit(id) is not { Life: { } l, Inside: 0, Garrison: 0 } u || !Commandable(u, c.Player) || u.Def.Speed.Raw == 0)
            {
                continue;
            }
            var b = c.Target != 0 ? BodyOf(c.Target) : NearestBody(u, Fixed.Frac(r[SoldierValue.ScavengeSearch], 10));
            if (b is null || Takes(u, b) == 0)
            {
                continue;
            }
            EndTasks(u, l);
            StartScavenge(u, l, b);
        }
    }

    // EndTasks ends whatever task u has (a new order of his player's, or he went down).
    private void EndTasks(Unit u, SoldierLife l)
    {
        if (l.Carrying != 0)
        {
            EndCarry(u, l, Unit(l.Carrying), false);
        }
        (l.Treating, l.TreatLeft, l.Scavenging, l.ScavengeLeft) = (0, 0, 0, 0);
    }

    // ---- RunWounds: states, crawling, the carried, bleeding, bodies, battles, ranks (before the cleanup) ----

    private void RunWounds()
    {
        if (!_soldiers || Content.Soldiers is not { } r)
        {
            return;
        }
        foreach (var u in _units)
        {
            if (u.Life is not { } l)
            {
                continue;
            }
            if (u.Alive)
            {
                Classify(u, l, r);
            }
            if (u.Alive && l.Wound == WoundState.Down)
            {
                if (l.Carrier != 0 && Unit(l.Carrier) is { Alive: true } c)
                {
                    u.Pos = c.Pos; // carried
                }
                else
                {
                    Crawl(u, l, r);
                }
                if (Tick >= l.BleedPausedUntil && --l.BleedLeft <= 0)
                {
                    u.HP = 0;
                    SoldierEvent(SoldierEventKind.BledOut, u, l.DownedBy, 0);
                    Credit(u, Unit(l.DownedBy), r);
                }
            }
            if (!u.Alive)
            {
                Died(u, l, r);
                continue;
            }
            if (l.InBattle && (Tick + u.Id) % 20 == 0)
            {
                Battle(u, l, r);
            }
            var rank = l.Xp >= r[SoldierValue.RankElite] ? SoldierRank.Elite : l.Xp >= r[SoldierValue.RankVeteran] ? SoldierRank.Veteran
                : l.Xp >= r[SoldierValue.RankRegular] ? SoldierRank.Regular : SoldierRank.Recruit;
            if (rank > l.Rank)
            {
                l.Rank = rank;
                SoldierEvent(SoldierEventKind.RankUp, u, 0, (int)rank);
                if (u.Squad != 0 && Squad(u.Squad) is { } s && Content.Squad is { } sr && LeaderOf(s, sr) != s.Leader)
                {
                    Rebuild(s, sr); // the highest rank leads
                }
            }
        }
        for (var i = 0; i < _bodies.Count; i++)
        {
            if (_bodies[i].Until <= Tick)
            {
                _soldierEvents.Add(new SoldierEvent(SoldierEventKind.BodyGone, _bodies[i].Id, 0, 0, _bodies[i].Pos));
                _bodies.RemoveAt(i--);
            }
        }
    }

    // Classify sets u's wound state from his HP: down only on foot on the map (inside he is wounded until he comes out).
    private void Classify(Unit u, SoldierLife l, SoldierRules r)
    {
        var max = (long)u.Def.MaxHp;
        var down = (long)u.HP * 100 < max * r[SoldierValue.DownPercent] && u.Inside == 0 && u.Garrison == 0;
        var state = down ? WoundState.Down : (long)u.HP * 100 <= max * r[SoldierValue.WoundedPercent] ? WoundState.Wounded : WoundState.Healthy;
        if (state == l.Wound)
        {
            return;
        }
        var was = l.Wound;
        l.Wound = state;
        l.BattleHurt |= state != WoundState.Healthy && l.InBattle;
        if (state == WoundState.Down)
        {
            EndTasks(u, l);
            Halt(u);
            u.ResetAi();
            l.BleedLeft = r[SoldierValue.BleedTicks];
            l.BleedPausedUntil = 0;
            l.DownedBy = u.LastHitTick == Tick ? u.LastHitBy : 0;
            l.TaskCell = CrawlTarget(u, r);
            SoldierEvent(SoldierEventKind.Down, u, l.DownedBy, u.HP);
            return;
        }
        if (was == WoundState.Down)
        {
            (l.BleedLeft, l.BleedPausedUntil, l.DownedBy, l.Carrier) = (0, 0, 0, 0); // up again (treated, or healed by other means)
        }
        if (state == WoundState.Wounded && was == WoundState.Healthy)
        {
            SoldierEvent(SoldierEventKind.Wounded, u, u.LastHitBy, u.HP);
        }
        else if (state == WoundState.Healthy)
        {
            SoldierEvent(SoldierEventKind.Healed, u, 0, u.HP);
        }
    }

    // CrawlTarget: the cell a downed man crawls to: the nearest squadmate (on his feet, on the map) within crawlReach, else the nearest
    // hard cover there, else where he lies.
    private Cell CrawlTarget(Unit u, SoldierRules r)
    {
        var reach = Fixed.Frac(r[SoldierValue.CrawlReach], 10);
        Unit? mate = null;
        if (u.Squad != 0 && Squad(u.Squad) is { } s)
        {
            foreach (var id in s.MemberList)
            {
                if (Unit(id) is { Inside: 0, Garrison: 0 } m && m != u && m.Alive && !IsDown(m) && Within(m.Pos, u.Pos, reach) &&
                    (mate is null || (m.Pos - u.Pos).LenSq() < (mate.Pos - u.Pos).LenSq()))
                {
                    mate = m;
                }
            }
        }
        if (mate is not null)
        {
            return Cell.Of(mate.Pos);
        }
        return HardCoverNear(u.Pos, ThreatOf(u), reach, r) ?? Cell.Of(u.Pos);
    }

    // Crawl moves a downed man (not carried) crawlSpeed toward his target (looked at again every second), over open ground only.
    private void Crawl(Unit u, SoldierLife l, SoldierRules r)
    {
        if ((Tick + u.Id) % 20 == 0)
        {
            l.TaskCell = CrawlTarget(u, r);
        }
        var to = l.TaskCell.Center() - u.Pos;
        var step = (long)Fixed.OneRaw * r[SoldierValue.CrawlSpeed] / 200; // tenths of a cell a second, 20 ticks a second
        var len = to.Len().Raw;
        if (len <= Fixed.OneRaw / 2 || step <= 0)
        {
            return; // there (beside his squadmate, or in the cover cell)
        }
        var next = len <= step ? l.TaskCell.Center() : u.Pos + to.WithLen(Fixed.FromRaw(step));
        var c = Cell.Of(next);
        if (Grid.Contains(c) && Grid.Passable(c))
        {
            u.Pos = next;
        }
    }

    // Died: a soldier dead on the map leaves a body with his leftover carried ammo; the man he carried is dropped, his carrier freed.
    private void Died(Unit u, SoldierLife l, SoldierRules r)
    {
        if (l.Carrying != 0)
        {
            EndCarry(u, l, Unit(l.Carrying), false);
        }
        if (l.Carrier != 0 && Unit(l.Carrier) is { Life: { } cl } carrier)
        {
            SoldierEvent(SoldierEventKind.CarryStop, carrier, u.Id, 0);
            cl.Carrying = 0;
            l.Carrier = 0;
        }
        if (u.Inside != 0 || u.Garrison != 0 || BodyOf(u.Id) is not null)
        {
            return;
        }
        var b = new Body(u.Id, u.Owner, u.Def.Archetype.Index, u.Pos, Tick + (uint)r[SoldierValue.BodyTicks]);
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            var w = weapons[i];
            if (w.Stock >= 0 && w.Effect.Carried > 0 && w.Effect.AmmoGroup != AmmoGroup.None && !FirstOfStock(weapons, i))
            {
                b.AmmoBy[(int)w.Effect.AmmoGroup] += u.Charges[w.Stock];
            }
        }
        while (_bodies.Count >= r[SoldierValue.BodyMax])
        {
            _soldierEvents.Add(new SoldierEvent(SoldierEventKind.BodyGone, _bodies[0].Id, 0, 0, _bodies[0].Pos));
            _bodies.RemoveAt(0); // the oldest goes first
        }
        _bodies.Add(b);
        _soldierEvents.Add(new SoldierEvent(SoldierEventKind.BodyLies, u.Id, 0, 0, u.Pos));
    }

    // Battle (once a second): a shot by or at anyone within battleReach keeps u's battle on; after battleCalmTicks without one he has
    // survived it (points, more if he was hurt during it).
    private void Battle(Unit u, SoldierLife l, SoldierRules r)
    {
        var reach = Fixed.Frac(r[SoldierValue.BattleReach], 10);
        var since = Tick >= 20 ? Tick - 20 : 0;
        foreach (var o in _units)
        {
            if (o != u && (o.LastShot > since || o.Life is { ShotAt: var s } && s > since) && Within(o.Pos, u.Pos, reach))
            {
                l.BattleShot = Tick;
                break;
            }
        }
        if (Tick - l.BattleShot >= (uint)r[SoldierValue.BattleCalmTicks])
        {
            l.Xp += r[SoldierValue.XpSurvive] + (l.BattleHurt || l.Wound != WoundState.Healthy ? r[SoldierValue.XpSurviveHurt] : 0);
            (l.InBattle, l.BattleHurt) = (false, false);
        }
    }

    // LeaderOf: who leads squad s: the highest rank among the members whose role leads (ties: the lowest ID), else its first member.
    private uint LeaderOf(Squad s, SquadRules rules)
    {
        uint leader = 0;
        var best = -1;
        foreach (var id in s.MemberList)
        {
            var m = Unit(id)!;
            var rank = (int)(m.Life?.Rank ?? SoldierRank.Recruit);
            if (rules.Leads(rules.RoleOf(m.Def.Tags)) && rank > best)
            {
                (leader, best) = (id, rank);
            }
        }
        return leader == 0 && s.MemberList.Count > 0 ? s.MemberList[0] : leader;
    }

    private void HashSoldiers(ref WordHash h)
    {
        var retreats = false;
        foreach (var s in _squads)
        {
            retreats |= s.Retreating;
        }
        if (_bodies.Count == 0 && !retreats)
        {
            return; // no bodies and no retreats: worlds without them hash as before
        }
        h.Put(0x5B0D);
        h.PutSigned(_bodies.Count);
        foreach (var b in _bodies)
        {
            b.HashInto(ref h);
        }
        foreach (var s in _squads)
        {
            if (!s.Retreating)
            {
                continue;
            }
            h.Put(s.Id);
            h.PutSigned(s.RetreatSpot.X);
            h.PutSigned(s.RetreatSpot.Y);
            h.PutSigned(s.RetreatThreat.X.Raw);
            h.PutSigned(s.RetreatThreat.Y.Raw);
            h.Put(s.RetreatBoundUntil);
            h.PutSigned(s.RetreatPair);
            h.PutSigned(s.RetreatPlaces.Count);
            foreach (var (u, c) in s.RetreatPlaces)
            {
                h.Put(u);
                h.PutSigned(c.X);
                h.PutSigned(c.Y);
            }
            h.PutSigned(s.RetreatMoving.Count);
            foreach (var u in s.RetreatMoving)
            {
                h.Put(u);
            }
        }
    }
}
