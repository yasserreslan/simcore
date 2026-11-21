using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// INF-E1b-2, the soldier's think (docs/design.md, "Infantry combat: the soldier's think"; the game repo's
// docs/plans/infantry-battle-rules.md sections 4.6, 5.5, 6, 7, 9.1, 14.1 to 14.3, 15 and 21): targets, facing, gaits and the back
// rule, position choice, grenades and the grenade reflex, friendly vehicles as cover. Called from ThinkSoldier and RunMinds
// (InfantryCombatSystem.cs); every loop is capped by a content number, every list walked in ID or cell order.
public sealed partial class World
{
    private const ulong BurstSalt = 0x1E62_0000_0000_0001UL; // 15.5 burst sizes and waits
    private const ulong RetargetSalt = 0x1E62_0000_0000_0002UL; // 15.3 a new target's delay
    private const ulong GrenadeSalt = 0x1E62_0000_0000_0003UL; // 14.1 where a grenade lands
    private const ulong ReflexSalt = 0x1E62_0000_0000_0004UL; // 14.3 the reflex's reaction

    private readonly List<Grenade> _grenades = [];
    private readonly List<Unit> _nearThink = [];
    private readonly List<(long D2, int Index)> _candidates = [];
    private long[] _heap = new long[64];
    private int[] _floodCost = [];
    private int[] _floodParent = [];
    private int _searchesThisTick;
    private ulong _coverVehicleOwners; // scratch, rebuilt each tick from the units
    private ulong[] _settledBits = new ulong[8];

    /// <summary>Counters for the firefight bench (output, not state): searches run, searches that waited for the cap, spots taken.</summary>
    internal (long Searches, long SearchWaited, long Moves, long Throws, long Reflexes) ThinkStats;

    /// <summary>The grenades thrown and not yet gone off (14.1).</summary>
    public IReadOnlyList<Grenade> Grenades => _grenades;

    // ---- The think (called from ThinkSoldier, after perception) ----

    // ThinkFight: the parts of the soldier's think that act (sections 6, 7, 14, 15): once he has reacted, his target, his body
    // facing, a grenade, his position; then his vehicle (7.6).
    private void ThinkFight(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        var reacted = f.ReactAt == 0 || Tick >= f.ReactAt;
        f.OutOfReach = OutOfReachNow(u, f, r);
        if (!reacted || !u.Alive || u.Inside != 0 || u.Garrison != 0 || u.Life is { Wound: WoundState.Down } or { Carrier: not 0 })
        {
            f.Face = -1;
            return;
        }
        ChooseTarget(u, f, r);
        ChooseFace(u, f, r);
        if (f.ReflexAt != 0 && Tick >= f.ReflexAt)
        {
            StartReflex(u, f, r);
        }
        if (!f.Reflex)
        {
            ThinkThrow(u, f, r);
            ThinkVehicle(u, f, r);
            ThinkPosition(u, f, r, false);
        }
    }

    // ---- Targets (15) ----

    // RulesTarget: the soldier's own think picks his targets (the order system leaves them to it).
    internal bool RulesTarget(Unit u) => Combat is not null && u.Fight is not null;

    // ChooseTarget (15.1 to 15.4): among the enemies he sees now within his weapon's range that it can damage, the best score
    // threat x hitChance x sector x focus x stick x overkill (64-bit products); a down enemy only when no able one is a target (threat
    // downThreat; never for a marksman, never under Hold fire or a Retreat); none under minScore's hit chance. A player's Attack is
    // obeyed (the order system fires at it); carrying or treating, he doesn't fire. A target lost (down, dead, out of sight) gives the
    // next one a delay of retargetMin to retargetMax ticks (no instant snap).
    private void ChooseTarget(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        if (u.Order.Kind is OrderKind.Attack or OrderKind.AttackGround || u.Def.Weapons.Count == 0)
        {
            return;
        }
        if (u.Life is { Busy: true } || u.Stance is Stance.HoldFire or Stance.Hide or Stance.ReturnFire)
        {
            if (u.Life is { Busy: true })
            {
                u.AutoTarget = 0;
            }
            return; // Hold fire and Hide (15.7) and Return fire stay the order system's
        }
        var squad = u.Squad != 0 ? Squad(u.Squad) : null;
        var mind = squad?.Mind;
        var marksman = r.Weapon(u.Def.Weapons[0].Effect) is { Class: CombatClass.Marksman };
        var lmg = r.Weapon(u.Def.Weapons[0].Effect) is { Class: CombatClass.Lmg }; // works along the enemy's line (16.2)
        var mayDown = !marksman && squad is not { Retreating: true };
        uint best = 0, bestDown = 0;
        long bestScore = -1, bestDownScore = -1, bestD2 = 0, bestDownD2 = 0;
        int valid = 0;
        var shooters = 0;
        if (squad is not null)
        {
            foreach (var id in squad.MemberList)
            {
                shooters += Unit(id) is { } m && Able(m) && m.Def.Weapons.Count > 0 ? 1 : 0;
            }
        }
        foreach (var k in f.ContactList)
        {
            valid += k.Enemy != 0 && k.Source == ContactSource.SeenNow && k.KnownAt <= Tick && Unit(k.Enemy) is { Alive: true } e0 &&
                e0.Life is not { Wound: WoundState.Down } && IsSpotted(e0, u.Owner) && PickWeapon(u, e0, true) >= 0 ? 1 : 0;
        }
        var shareMax = Math.Max(r[CombatValue.TargetShareMin], (shooters + Math.Max(1, valid) - 1) / Math.Max(1, valid));
        foreach (var k in f.ContactList)
        {
            if (k.Enemy == 0 || k.Source != ContactSource.SeenNow || k.KnownAt > Tick || Unit(k.Enemy) is not { Alive: true } e || !IsSpotted(e, u.Owner))
            {
                continue;
            }
            var wi = PickWeapon(u, e, true);
            if (wi < 0)
            {
                continue;
            }
            var down = e.Life is { Wound: WoundState.Down };
            if (down && !mayDown)
            {
                continue;
            }
            var w = u.Def.Weapons[wi];
            long threat = down ? r[CombatValue.TargetDownThreat] : Danger(k, u, r);
            if (!down && (k.Class == CombatClass.Lmg && e.Fight is { Posture: Posture.Prone } ||
                k.Class == CombatClass.Marksman && e.LastShot != 0 && Tick - e.LastShot < (uint)r[CombatValue.TargetMarksmanTicks] ||
                k.Class == CombatClass.Launcher && Unit(e.AutoTarget) is { Owner: var vo } vt && vo == u.Owner && vt.Life is null))
            {
                threat = threat * r[CombatValue.TargetHeavyThreat] / 100;
            }
            var hit = w.Effect.Accuracy > 0 ? HitChance(u, e, w) : 1000;
            var focus = mind is not null && mind.Focus == e.Id;
            var count = 0;
            if (squad is not null)
            {
                foreach (var id in squad.MemberList)
                {
                    count += id != u.Id && Unit(id) is { AutoTarget: var at } && at == e.Id ? 1 : 0;
                }
            }
            var over = focus ? count * 2 >= shooters && count >= shareMax : count >= shareMax;
            var stick = lmg ? (e.Id == f.LastTarget && valid > 1 && f.BurstLeft <= 0 ? r[CombatValue.TargetOverkill] : 100) : u.AutoTarget == e.Id && !down ? r[CombatValue.TargetStick] : 100;
            var score = threat * Math.Max(0, hit) * SectorOf(u, squad, e, r) * (focus ? r[CombatValue.TargetFocus] : 100) * stick * (over ? r[CombatValue.TargetOverkill] : 100);
            if (hit < r[CombatValue.TargetMinScore])
            {
                score = -1; // under the floor: no aimed shot (suppressive fire is the squad's, E1b-3)
            }
            var d2 = (e.Pos - u.Pos).LenSq();
            ref var b = ref down ? ref bestDown : ref best;
            ref var bs = ref down ? ref bestDownScore : ref bestScore;
            ref var bd = ref down ? ref bestDownD2 : ref bestD2;
            if (score >= 0 && (score > bs || (score == bs && (d2 < bd || (d2 == bd && e.Id < b)))))
            {
                (b, bs, bd) = (e.Id, score, d2);
            }
        }
        var pick = best != 0 ? best : valid > 0 ? 0 : bestDown;
        if (pick == 0 && Unit(u.AutoTarget) is { Alive: true, Def.IsStructure: true })
        {
            return; // a building the order system found (no soldier or vehicle to shoot instead)
        }
        if (pick == u.AutoTarget)
        {
            f.NoTargetSince = pick == 0 && f.NoTargetSince == 0 ? Tick : pick == 0 ? f.NoTargetSince : 0;
            return;
        }
        var prev = u.AutoTarget != 0 ? u.AutoTarget : Tick - f.LastTargetTick <= (uint)r[CombatValue.TargetNoTargetTicks] ? f.LastTarget : 0;
        var lost = prev != 0 && prev != pick && (Unit(prev) is not { Alive: true } old || old.Life is { Wound: WoundState.Down } || !IsSpotted(old, u.Owner));
        if (lost && pick != 0)
        {
            var lo = r[CombatValue.TargetRetargetMin];
            var hi = Math.Max(lo, r[CombatValue.TargetRetargetMax]);
            f.FireAt = Math.Max(f.FireAt, Tick + (uint)(lo + (int)(Rand.Hash(Match.Setup.Seed, Tick, u.Id, RetargetSalt) % (ulong)(hi - lo + 1))));
            f.BurstLeft = 0; // the burst ends there: the rest of the magazine is kept
        }
        u.AutoTarget = pick;
        f.NoTargetSince = pick == 0 ? Tick : 0;
    }

    // SectorOf (15.2): targets projected on the squad's line (across its axis); the able shooter nearest to e along it (ties by ID)
    // has e in his sector (sectorIn), the second next to it (sectorNext), the rest sectorOut. A loose soldier, or no axis: 100.
    private int SectorOf(Unit u, Squad? s, Unit e, InfantryCombatRules r)
    {
        if (s is not { Mind: { Axis: >= 0 } mind })
        {
            return 100;
        }
        var line = Directions[(mind.Axis + 8) & 31];
        long Along(Vec p) => (p.X.Raw * line.X + p.Y.Raw * line.Y) >> 10;
        var at = Along(e.Pos);
        var mine = Math.Abs(Along(u.Pos) - at);
        var rank = 0;
        foreach (var id in s.MemberList)
        {
            if (id == u.Id || Unit(id) is not { } m || !Able(m) || m.Def.Weapons.Count == 0)
            {
                continue;
            }
            var d = Math.Abs(Along(m.Pos) - at);
            rank += d < mine || (d == mine && id < u.Id) ? 1 : 0;
        }
        return rank == 0 ? r[CombatValue.TargetSectorIn] : rank == 1 ? r[CombatValue.TargetSectorNext] : r[CombatValue.TargetSectorOut];
    }

    // NoteBurst (each small-arms shot of a soldier): a burst of the weapon's size, then a wait (15.5); longer on the move.
    private void NoteBurst(Unit u, WeaponDef w, InfantryCombatRules r)
    {
        if (u.Fight is not { } f)
        {
            return;
        }
        var cls = r.Weapon(w.Effect)?.Class ?? CombatClass.Rifle;
        if (f.BurstLeft <= 0)
        {
            var (lo, hi) = cls switch
            {
                CombatClass.Smg => (r[CombatValue.TargetBurstSmg], r[CombatValue.TargetBurstSmg]),
                CombatClass.Lmg => (r[CombatValue.TargetBurstLmgMin], r[CombatValue.TargetBurstLmgMax]),
                CombatClass.Pistol => (r[CombatValue.TargetBurstPistolMin], r[CombatValue.TargetBurstPistolMax]),
                CombatClass.Marksman or CombatClass.Launcher or CombatClass.Crew => (1, 1),
                _ => (r[CombatValue.TargetBurstMin], r[CombatValue.TargetBurstMax]),
            };
            f.BurstLeft = Draw(lo, hi, u.Id, BurstSalt);
        }
        if (--f.BurstLeft > 0)
        {
            return;
        }
        var moving = Walking(u);
        var wait = moving ? Draw(r[CombatValue.TargetMoveWaitMin], r[CombatValue.TargetMoveWaitMax], u.Id, BurstSalt + 1)
            : Draw(r[CombatValue.TargetWaitMin], r[CombatValue.TargetWaitMax], u.Id, BurstSalt + 1);
        f.FireAt = Math.Max(f.FireAt, Tick + (uint)wait);
    }

    private int Draw(int lo, int hi, uint id, ulong salt)
    {
        hi = Math.Max(lo, hi);
        return lo + (int)(Rand.Hash(Match.Setup.Seed, Tick, id, salt) % (ulong)(hi - lo + 1));
    }

    // ThinkHolds: the think's reasons not to fire this tick (15.3, 15.5, 7.1, 14.1): a burst's wait or a new target's delay, a big
    // turn, a throw, carrying or treating.
    private static bool ThinkHolds(Unit u, SoldierFight f, uint tick) =>
        tick < f.FireAt || f.TurnLeft > 0 || f.ThrowAt != 0 || f.Reflex || u.Life is { Busy: true };

    // ---- Facing (7.1) ----

    // ChooseFace: in contact his body faces his order's aim (Attack Ground's cell, Attack's target; decision 1200), his target, else the nearest known contact of faceDanger or more, else his axis, else
    // the squad's; out of contact, where he moves (-1).
    private void ChooseFace(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        if (!f.InContact)
        {
            f.Face = -1;
            return;
        }
        if (u.Order.Kind == OrderKind.AttackGround)
        {
            var g = u.Order.Cell.Center(); // a player's fire at the ground: he faces where he shoots (7.1)
            f.Face = Dir32(g.X.Raw - u.Pos.X.Raw, g.Y.Raw - u.Pos.Y.Raw);
            return;
        }
        if (Unit(u.Order.Kind == OrderKind.Attack ? u.Order.Target : u.AutoTarget) is { Alive: true } t)
        {
            f.Face = Dir32(t.Pos.X.Raw - u.Pos.X.Raw, t.Pos.Y.Raw - u.Pos.Y.Raw);
            return;
        }
        long bestD2 = long.MaxValue;
        var face = -1;
        foreach (var k in f.ContactList)
        {
            if (k.KnownAt > Tick || Danger(k, u, r) < r[CombatValue.FaceFaceDanger])
            {
                continue;
            }
            var q = k.At.Center();
            long dx = q.X.Raw - u.Pos.X.Raw, dy = q.Y.Raw - u.Pos.Y.Raw;
            if (dx * dx + dy * dy < bestD2)
            {
                (bestD2, face) = (dx * dx + dy * dy, Dir32(dx, dy));
            }
        }
        f.Face = face >= 0 ? face : f.Axis >= 0 ? f.Axis : u.Squad != 0 && Squad(u.Squad) is { Mind.Axis: >= 0 and var a } ? a : -1;
    }

    // OutOfReachNow (7.3, exception 1): beyond outOfReachPercent of the range of every known contact of faceDanger or more, or no
    // enemy has spotted him for unspottedTicks.
    private bool OutOfReachNow(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        if (f.SpottedAt == 0 || Tick - f.SpottedAt >= (uint)r[CombatValue.FaceUnspottedTicks])
        {
            return true;
        }
        foreach (var k in f.ContactList)
        {
            if (k.KnownAt > Tick || Danger(k, u, r) < r[CombatValue.FaceFaceDanger])
            {
                continue;
            }
            ReachOf(k, r, out var range);
            var far = range * r[CombatValue.FaceOutOfReachPercent] / 100;
            var q = k.At.Center();
            long dx = q.X.Raw - u.Pos.X.Raw, dy = q.Y.Raw - u.Pos.Y.Raw;
            if (dx * dx + dy * dy <= far * far)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The 32-direction index of a <see cref="Facing"/> (Facing 0 is north, the table's 0 is east).</summary>
    internal static int FacingToDir(int facing) => (facing + 24) & 31;

    /// <summary>The <see cref="Facing"/> of a 32-direction index.</summary>
    internal static int DirToFacing(int dir) => (dir + 8) & 31;

    // ---- Gaits and the back rule (7.2, 7.3) ----

    // FreeToRun (7.3's exceptions): he may turn his back and run: out of contact; out of reach or unspotted; the grenade reflex; not
    // a fighter (no weapon, carrying a man). Routing and the run modifier wait for E1b-3/4 (no morale, no run order yet).
    private static bool FreeToRun(Unit u, SoldierFight f) =>
        !f.InContact || f.OutOfReach || f.Reflex || u.Def.Weapons.Count == 0 || u.Life is { Carrying: not 0 };

    // GaitPercent: his gait this step (stored on him) and its speed percent: in contact by the angle between his move and his body
    // facing (walk within advanceSteps, strafe under backSteps, else backpedal; crouched at suppression 30+: crouchGait of it); a
    // dash to his own spot crouch-runs when it lies within runSteps of the axis; free to run: run, or sprint out of contact on his
    // own spot's dash (sprintTicks, then sprintRest of running; never wounded); prone and pinned hard: crawl. The LMG gunner lmgSpeed.
    internal int GaitPercent(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        var d = u.Intent.Desired;
        var move = Dir32(d.X.Raw, d.Y.Raw);
        f.MoveFacing = move >= 0 ? DirToFacing(move) : u.Facing;
        var s = f.Suppression;
        int pct;
        if (f.Posture == Posture.Prone && s >= r[CombatValue.PinnedHard])
        {
            (f.Gait, pct) = (Gait.Crawl, r[CombatValue.CrawlSpeed]);
        }
        else if (!f.InContact && f.Alert && move >= 0 && AxisOrContact(u, f) is >= 0 and var toward && DirSteps(move, toward) <= r[CombatValue.FaceAdvanceSteps])
        {
            (f.Gait, pct) = (Gait.Walk, r[CombatValue.GaitWalk]); // alert, toward the enemy: a walk, weapon up (rules 25, scene 34)
        }
        else if (FreeToRun(u, f) || f.Face < 0 || move < 0)
        {
            var sprint = !f.InContact && f.Alert && f.SpotMoving && u.Life is not { Wound: not WoundState.Healthy } && Tick >= f.SprintRest;
            if (sprint && f.SprintFrom == 0)
            {
                f.SprintFrom = Tick;
            }
            if (f.SprintFrom != 0 && (!sprint || Tick - f.SprintFrom >= (uint)r[CombatValue.GaitSprintTicks]))
            {
                f.SprintRest = Tick + (uint)r[CombatValue.GaitSprintRest];
                (f.SprintFrom, sprint) = (0, false);
            }
            (f.Gait, pct) = sprint ? (Gait.Sprint, r[CombatValue.GaitSprint]) : s >= r[CombatValue.Shaken] && f.InContact ? (Gait.CrouchRun, r[CombatValue.GaitCrouchRun])
                : (Gait.Run, r[CombatValue.GaitRun]);
        }
        else if (f.SpotMoving && f.Axis >= 0 && DirSteps(move, f.Axis) <= r[CombatValue.FaceRunSteps])
        {
            (f.Gait, pct) = (Gait.CrouchRun, r[CombatValue.GaitCrouchRun]); // a dash to close cover, forward or across
        }
        else
        {
            var a = DirSteps(move, f.Face);
            (f.Gait, pct) = a <= r[CombatValue.FaceAdvanceSteps] ? (Gait.Walk, r[CombatValue.GaitWalk])
                : a < r[CombatValue.FaceBackSteps] ? (Gait.Strafe, r[CombatValue.GaitStrafe]) : (Gait.Backpedal, r[CombatValue.GaitBackpedal]);
            if (s >= r[CombatValue.Shaken])
            {
                pct = pct * r[CombatValue.GaitCrouchGait] / 100;
            }
        }
        if (u.Def.Weapons.Count > 0 && r.Weapon(u.Def.Weapons[0].Effect) is { Class: CombatClass.Lmg })
        {
            pct = pct * r[CombatValue.GaitLmgSpeed] / 100;
        }
        return pct;
    }

    // FacesMove: the body turns with the move (out of contact, a run, a dash, a crawl) or keeps to his facing (walk, strafe, backpedal).
    internal static bool FacesMove(SoldierFight f) => f.Face < 0 || f.Gait is not (Gait.Walk or Gait.Strafe or Gait.Backpedal);

    /// <summary>Whether u may fire while he moves (7.4): walking, strafing or backpedalling; never a marksman, launcher or crew
    /// weapon; the LMG not backpedalling.</summary>
    internal bool FiresOnTheMove(Unit u)
    {
        if (u.Fight is not { } f || Combat is not { } r || f.Gait is not (Gait.Walk or Gait.Strafe or Gait.Backpedal) || u.Def.Weapons.Count == 0)
        {
            return false;
        }
        var cls = r.Weapon(u.Def.Weapons[0].Effect)?.Class ?? CombatClass.Unknown;
        return cls is not (CombatClass.Marksman or CombatClass.Launcher or CombatClass.Crew or CombatClass.Unknown) &&
            !(cls == CombatClass.Lmg && f.Gait == Gait.Backpedal);
    }

    // GaitAim and GaitHit: on the move his gait's accuracy and the hit factor on him replace his posture's (7.2).
    private static int GaitAim(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        var aim = f.Gait switch
        {
            Gait.Walk => r[CombatValue.GaitWalkAim],
            Gait.Strafe => r[CombatValue.GaitStrafeAim],
            Gait.Backpedal => r[CombatValue.GaitBackAim],
            _ => 100,
        };
        if (f.Suppression >= r[CombatValue.Shaken])
        {
            aim += r[CombatValue.GaitCrouchAimPlus];
        }
        return u.Def.Weapons.Count > 0 && r.Weapon(u.Def.Weapons[0].Effect) is { Class: CombatClass.Lmg } ? aim * r[CombatValue.GaitLmgHipAim] / 100 : aim;
    }

    private static int GaitHit(SoldierFight f, InfantryCombatRules r) => f.Gait switch
    {
        Gait.CrouchRun => r[CombatValue.GaitCrouchHit],
        Gait.Run or Gait.Sprint => r[CombatValue.GaitRunHit],
        Gait.Crawl => r[CombatValue.GaitCrawlHit],
        _ => f.Suppression >= r[CombatValue.Shaken] ? r[CombatValue.GaitCrouchHit] : 100,
    };

    // ---- Each tick (RunMinds) ----

    // TickFight: the big turn's ticks, a throw leaving his hand, the reflex's end, the body turning to his facing while he stands.
    private void TickFight(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        if (!Walking(u))
        {
            f.Gait = Gait.None;
        }
        if (f.TurnLeft > 0)
        {
            f.TurnLeft--;
        }
        else if (f.Face >= 0 && f.InContact && !FacesMove(f) || f.Face >= 0 && !Walking(u) && u.AutoTarget == 0)
        {
            var want = DirToFacing(f.Face);
            if (Math.Abs(Facing.Diff(u.Facing, want)) > r[CombatValue.FaceBigTurn])
            {
                f.TurnLeft = r[CombatValue.FaceBigTurnTicks]; // over 90 degrees: no firing meanwhile
            }
            if (!Walking(u))
            {
                u.Facing = Facing.TurnToward(u.Facing, want, u.Def.TurnRate);
            }
        }
        if (f.ThrowAt != 0 && Tick >= f.ThrowAt)
        {
            ReleaseGrenade(u, f, r);
        }
        if (f.Reflex && (!Walking(u) || NoGrenadeNear(f.ReflexFrom)))
        {
            f.Reflex = false; // out of it (or it went off): the rules apply again
        }
        if (f.Vehicle != 0 && Unit(f.Vehicle) is { } v && Walking(u))
        {
            u.ConvoyTick = Tick; // at the vehicle's pace (7.6), read by the movement
            u.ConvoyCap = Math.Max(1, v.DriveSpeed.Raw);
        }
    }

    // ---- Position choice (6) ----

    // Horizon (6.2): how long he expects to stay exposed: in a squad in contact hFight (the squad's choice is E1b-3's), loose hLoose,
    // alert hAlert; wounded x woundedH.
    private int Horizon(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        var h = !f.InContact ? r[CombatValue.PosHAlert] : u.Squad != 0 ? r[CombatValue.PosHFight] : r[CombatValue.PosHLoose];
        return u.Life is { Wound: WoundState.Wounded } ? h * r[CombatValue.PosWoundedH] / 100 : h;
    }

    // MayChoose: the position rule is for the moves he chooses on his own (6): idle or on an attack-move, no chase, on foot, able,
    // not busy, with a weapon; never on the player's move, and never on Hide (INF-MOVE-3: the stance never moves on its own, Unit.cs).
    private bool MayChoose(Unit u) =>
        u.Stance != Stance.Hide && (u.Order.Kind == OrderKind.Idle && (u.Squad == 0 || Squad(u.Squad) is not { } s || s.Order == OrderKind.Idle) ||
         u.Order.Kind == OrderKind.AttackMove && u.AutoTarget != 0) && u.Ai == AiState.None && u.Def.Speed.Raw > 0 && u.Def.Weapons.Count > 0 &&
        u.Life is not { Busy: true } && u.Deploy == DeployState.Stowed && u.Fight is { ThrowAt: 0 };

    // ThinkPosition: the spot he holds or goes to (6.5), and a search when one is due (at most searchesPerTick a tick: the rest are
    // first in line the next tick).
    private void ThinkPosition(Unit u, SoldierFight f, InfantryCombatRules r, bool waited)
    {
        if (!MayChoose(u) || (!f.InContact && !f.Alert) || f.Vehicle != 0)
        {
            (f.HasSpot, f.SpotMoving) = (false, false); // calm again, or the player's move: back to his place in the squad
            f.SearchWaits = false;
            return;
        }
        var me = Cell.Of(u.Pos);
        if (f.HasSpot && f.SpotMoving)
        {
            if (me == f.Spot && !u.Moving)
            {
                (f.SpotMoving, f.SpotSince) = (false, Tick);
                ThinkStats.Moves++;
            }
            else if (!u.Moving && !u.PathPending)
            {
                (f.Unreachable, f.UnreachableUntil) = (f.Spot, Tick + (uint)r[CombatValue.PosUnreachableTicks]);
                (f.HasSpot, f.SpotMoving) = (false, false); // no path there: keep his spot this think
            }
            else if (ZoneValue(f.Spot) < ZoneValue(me) || !ClaimFree(u, f.Spot, r))
            {
                return; // on his way: he keeps going
            }
        }
        var here = Exposure(u, me, BestPosture(u, f, me, r));
        var due = !f.HasSpot || Tick - f.LastSearch >= (uint)(f.InContact ? r[CombatValue.PosSearchTicks] : r[CombatValue.PosGoodTicks]); // no spot yet: at once
        if (due && f.InContact && f.HasSpot && !f.SpotMoving && InCoverToward(me, f.Axis, r) &&
            here * 100 <= f.SearchExposure * r[CombatValue.PosRisePercent])
        {
            due = Tick - f.LastSearch >= (uint)r[CombatValue.PosGoodTicks]; // his spot is still good: every 4 s
        }
        if (f.HasSpot && !f.SpotMoving && Tick - f.SpotSince < (uint)r[CombatValue.PosStayTicks] &&
            here * 100 < f.SearchExposure * r[CombatValue.PosDoublePercent])
        {
            due = false; // just there: he stays, unless it got twice as bad
        }
        if (!due && !waited)
        {
            return;
        }
        if (_searchesThisTick >= r[CombatValue.PosSearchesPerTick])
        {
            if (!f.SearchWaits)
            {
                ThinkStats.SearchWaited++;
            }
            f.SearchWaits = true;
            return;
        }
        _searchesThisTick++;
        f.SearchWaits = false;
        Search(u, f, r, me, here);
    }

    // Search (6.1, 6.3, 6.4): a bounded flood from his cell; the cover and concealed cells it reaches within the radius (and spots
    // behind friendly vehicles, 5.5), nearest 16 first; the worth-it test against staying, then the lowest score.
    private void Search(Unit u, SoldierFight f, InfantryCombatRules r, Cell me, long here)
    {
        ThinkStats.Searches++;
        (f.LastSearch, f.SearchExposure) = (Tick, here);
        var axis = AxisOrContact(u, f);
        if (axis < 0 && ZoneValue(me) == 0)
        {
            Claim(u, f, me, false);
            return;
        }
        var effective = (f.ShotNear2 != 0 && Tick - f.ShotNear2 < (uint)r[CombatValue.EffectiveTicks]) ||
            (u.LastHitTick != 0 && Tick - u.LastHitTick < (uint)r[CombatValue.EffectiveHitTicks]);
        var radius = effective ? r[CombatValue.PosRadiusFire] : f.InContact ? r[CombatValue.PosRadiusContact] : r[CombatValue.PosRadiusCalm];
        var maxDash = effective ? r[CombatValue.PosDashFire] : f.InContact ? r[CombatValue.PosDashContact] : r[CombatValue.PosDashCalm];
        var h = Horizon(u, f, r);
        Flood(me, radius, r[CombatValue.PosFloodCells]);
        var side = 2 * radius + 1;
        _candidates.Clear();
        for (var i = 0; i < side * side; i++)
        {
            if (_floodCost[i] < 0)
            {
                continue;
            }
            var c = new Cell(me.X - radius + i % side, me.Y - radius + i / side);
            if (c == me || (c == f.Unreachable && Tick < f.UnreachableUntil) || !CandidateCell(u, c, axis, r))
            {
                continue;
            }
            long dx = c.X - me.X, dy = c.Y - me.Y;
            _candidates.Add((dx * dx + dy * dy, i));
        }
        _candidates.Sort((p, q) => p.D2 != q.D2 ? p.D2.CompareTo(q.D2) : p.Index.CompareTo(q.Index)); // ties: row, then column
        var max = Math.Min(_candidates.Count, r[CombatValue.PosMaxCandidates]);
        var bar = here * h * (100 - r[CombatValue.PosGainNeeded]);
        var best = me;
        var bestScore = long.MaxValue;
        for (var ci = 0; ci < max; ci++)
        {
            var idx = _candidates[ci].Index;
            var c = new Cell(me.X - radius + idx % side, me.Y - radius + idx / side);
            if (!ClaimFree(u, c, r))
            {
                continue;
            }
            var dir = Dir32(c.X - me.X, c.Y - me.Y);
            var (pct, hitF) = MoveGait(u, f, dir, axis, r);
            var posture = BestPosture(u, f, c, r);
            var t = MoveTicks(u, _floodCost[idx], pct) + ChangeTicks(f.PostureTo, Posture.Stand, r) + ChangeTicks(Posture.Stand, posture, r);
            if (t >= h || t > maxDash)
            {
                continue;
            }
            var there = Exposure(u, c, posture);
            var path = PathExposure(u, me, radius, idx, r) * hitF / 100;
            var cost = path * t + there * (h - t);
            if (cost * 100 >= bar)
            {
                continue; // 6.1: not worth it
            }
            var score = cost + Terms(u, c, h, r);
            if (score < bestScore)
            {
                (best, bestScore) = (c, score);
            }
        }
        Claim(u, f, best, best != me);
    }

    // AxisOrContact: his axis, else his squad's, else (alert: nobody has him in reach yet) the direction to his main contact.
    private int AxisOrContact(Unit u, SoldierFight f)
    {
        if (f.Axis >= 0)
        {
            return f.Axis;
        }
        if (u.Squad != 0 && Squad(u.Squad) is { Mind.Axis: >= 0 and var a })
        {
            return a;
        }
        return MainContact(u) is { } q ? Dir32(q.X.Raw - u.Pos.X.Raw, q.Y.Raw - u.Pos.Y.Raw) : -1;
    }

    // Claim takes cell c as his spot; going there asks the movement for a real path (a failed one marks it unreachable, 6.3).
    private void Claim(Unit u, SoldierFight f, Cell c, bool go)
    {
        (f.Spot, f.HasSpot, f.SpotMoving) = (c, true, go);
        if (go)
        {
            RequestRoute(u, c);
        }
        else
        {
            f.SpotSince = Tick;
        }
    }

    // CandidateCell (6.3): cover facing the axis within coverSteps (any side), concealment, or a spot behind a friendly vehicle.
    private bool CandidateCell(Unit u, Cell c, int axis, InfantryCombatRules r)
    {
        if (axis < 0)
        {
            return ZoneValue(c) == 0;
        }
        if (InCoverToward(c, axis, r) || (_cover is { } m && m.Density(c.Y * m.W + c.X) >= r[CombatValue.ConcealDensity]))
        {
            return true;
        }
        return VehicleCover(c, axis, u.Owner, r) != CoverKind.None;
    }

    // InCoverToward: a cover side within coverSteps of direction d (5.1's 8 sides are 4 table steps apart).
    private bool InCoverToward(Cell c, int d, InfantryCombatRules r)
    {
        if (d < 0 || _cover is not { } m || !Grid.Contains(c))
        {
            return false;
        }
        var i = c.Y * m.W + c.X;
        for (var s = 0; s < 8; s++)
        {
            if (DirSteps(s * 4, d) <= r[CombatValue.PosCoverSteps] && (m.Side(i, s) != CoverKind.None || m.Crest(i, s)))
            {
                return true;
            }
        }
        return false;
    }

    // BestPosture: the posture the table (9.1 rows 7 to 10) would give him at c: kneel at low cover toward his axis, stand at high,
    // in the open prone under effective fire from beyond 70% of the shooter's range (prone allowed), else kneel.
    private Posture BestPosture(Unit u, SoldierFight f, Cell c, InfantryCombatRules r)
    {
        var side = f.Axis >= 0 ? CoverToward(c, f.Axis, r) : CoverKind.None;
        if (side == CoverKind.None && f.Axis >= 0)
        {
            side = VehicleCover(c, f.Axis, u.Owner, r);
        }
        if (side == CoverKind.Low)
        {
            return Posture.Kneel;
        }
        if (side is CoverKind.High or CoverKind.Soft)
        {
            return Posture.Stand;
        }
        var effective = (f.ShotNear2 != 0 && Tick - f.ShotNear2 < (uint)r[CombatValue.EffectiveTicks]) ||
            (u.LastHitTick != 0 && Tick - u.LastHitTick < (uint)r[CombatValue.EffectiveHitTicks]);
        return f.InContact && effective && f.ShotFromPercent > r[CombatValue.ProneFireRangePercent] && ProneAllowed(u, c) ? Posture.Prone : Posture.Kneel;
    }

    // MoveGait: the gait speed and the hit factor of a move in direction dir (7.2, 7.3): in contact a dash (crouch-run) within
    // runSteps of the axis, else the back rule's walk, strafe or backpedal against his facing (crouched at 30+); out of contact a run.
    private (int Pct, int Hit) MoveGait(Unit u, SoldierFight f, int dir, int axis, InfantryCombatRules r)
    {
        if (!f.InContact && axis >= 0 && DirSteps(dir, axis) <= r[CombatValue.FaceAdvanceSteps])
        {
            return (r[CombatValue.GaitWalk], 100); // alert, toward the enemy: a walk
        }
        if (FreeToRun(u, f))
        {
            return (r[CombatValue.GaitRun], r[CombatValue.GaitRunHit]);
        }
        if (axis >= 0 && DirSteps(dir, axis) <= r[CombatValue.FaceRunSteps])
        {
            return (r[CombatValue.GaitCrouchRun], r[CombatValue.GaitCrouchHit]);
        }
        var a = DirSteps(dir, f.Face >= 0 ? f.Face : axis);
        var pct = a <= r[CombatValue.FaceAdvanceSteps] ? r[CombatValue.GaitWalk] : a < r[CombatValue.FaceBackSteps] ? r[CombatValue.GaitStrafe] : r[CombatValue.GaitBackpedal];
        return f.Suppression >= r[CombatValue.Shaken] ? (pct * r[CombatValue.GaitCrouchGait] / 100, r[CombatValue.GaitCrouchHit]) : (pct, 100);
    }

    // MoveTicks: ticks to walk a flood cost (10 a cell) at pct of his speed, rounded up.
    private static int MoveTicks(Unit u, int cost, int pct)
    {
        var per = Math.Max(1L, u.Def.Speed.Raw * pct / 100);
        return (int)Math.Min(int.MaxValue / 4, (cost * (long)Fixed.OneRaw / 10 + per - 1) / per);
    }

    // PathExposure (6.1): the mean exposure, standing, sampled every pathSample cells back along the flood's way to the candidate
    // (at most 4 samples), or where he stands for a step.
    private long PathExposure(Unit u, Cell me, int radius, int idx, InfantryCombatRules r)
    {
        var side = 2 * radius + 1;
        long sum = 0;
        var n = 0;
        var step = Math.Max(1, r[CombatValue.PosPathSample]);
        var k = 0;
        for (var i = _floodParent[idx]; i >= 0 && _floodParent[i] >= 0 && n < 4; i = _floodParent[i])
        {
            if (++k % step == 0)
            {
                sum += Exposure(u, new Cell(me.X - radius + i % side, me.Y - radius + i / side), Posture.Stand);
                n++;
            }
        }
        return n > 0 ? sum / n : Exposure(u, me, Posture.Stand);
    }

    // Terms (6.4): no line of fire to his main contact (+noFireLine x H; an LMG or marksman noFireLineHeavy), bunching (spreadTerm x H
    // with two or more squadmates within spreadReach). The range band's term waits for the range bands (0 today).
    private long Terms(Unit u, Cell c, int h, InfantryCombatRules r)
    {
        long t = 0;
        if (MainContact(u) is { } q && !FireLineFrom(c, q))
        {
            var heavy = r.Weapon(u.Def.Weapons[0].Effect) is { Class: CombatClass.Lmg or CombatClass.Marksman };
            t += (long)(heavy ? r[CombatValue.PosNoFireLineHeavy] : r[CombatValue.PosNoFireLine]) * h;
        }
        if (u.Squad != 0 && Squad(u.Squad) is { } s)
        {
            var near = 0;
            var reach = (long)r[CombatValue.PosSpreadReach] * Fixed.OneRaw / 10;
            var p = c.Center();
            foreach (var id in s.MemberList)
            {
                near += id != u.Id && Unit(id) is { } m && Able(m) && (m.Pos - p).LenSq() <= reach * reach ? 1 : 0;
            }
            t += near >= 2 ? (long)r[CombatValue.PosSpreadTerm] * h : 0;
        }
        return t;
    }

    // MainContact: where his target is, else his most dangerous known contact (ties: the nearer, the lower ID); null with none.
    private Vec? MainContact(Unit u)
    {
        if (Unit(u.AutoTarget) is { Alive: true } t)
        {
            return t.Pos;
        }
        var f = u.Fight!;
        Vec? best = null;
        var bd = -1;
        foreach (var k in f.ContactList)
        {
            var d = k.KnownAt <= Tick ? Danger(k, u, Combat!) : -1;
            if (d > bd)
            {
                (best, bd) = (k.At.Center(), d);
            }
        }
        return best;
    }

    // FireLineFrom: a rifleman's line from cell c to point q clears the terrain (always on a flat map).
    private bool FireLineFrom(Cell c, Vec q)
    {
        if (Ground is not { } g)
        {
            return true;
        }
        var p = c.Center();
        return LineOfSight(p, Fixed.FromRaw(g.HeightAt(p).Raw + EyeRaw), q, Fixed.FromRaw(g.HeightAt(q).Raw + BodyRaw));
    }

    // ClaimFree (6.3): not a squadmate's spot, not within minSpacing of one (coverSpacing behind hard cover facing the axis), not in
    // front of a squadmate's line of fire (within fireLineClear of the line to his target, nearer it than him).
    private bool ClaimFree(Unit u, Cell c, InfantryCombatRules r)
    {
        if (u.Squad == 0 || Squad(u.Squad) is not { } s)
        {
            return true;
        }
        var hard = u.Fight!.Axis >= 0 && CoverToward(c, u.Fight.Axis, r) is CoverKind.Low or CoverKind.High;
        var spacing = (long)(hard ? r[CombatValue.PosCoverSpacing] : r[CombatValue.PosMinSpacing]);
        var clear = (long)r[CombatValue.PosFireLineClear] * Fixed.OneRaw / 10;
        var p = c.Center();
        foreach (var id in s.MemberList)
        {
            if (id == u.Id || Unit(id) is not { Fight: { } mf } m || !Able(m))
            {
                continue;
            }
            if (mf.HasSpot)
            {
                long dx = mf.Spot.X - c.X, dy = mf.Spot.Y - c.Y;
                if ((dx * dx + dy * dy) * 100 < spacing * spacing)
                {
                    return false;
                }
            }
            if (Unit(m.AutoTarget) is { Alive: true } t && NearLine(p, m.Pos, t.Pos, clear) && (t.Pos - p).LenSq() < (t.Pos - m.Pos).LenSq())
            {
                return false;
            }
        }
        return true;
    }

    // NearLine: point p lies within `clear` (raw) of the segment a-b.
    private static bool NearLine(Vec p, Vec a, Vec b, long clear)
    {
        long bx = b.X.Raw - a.X.Raw, by = b.Y.Raw - a.Y.Raw, px = p.X.Raw - a.X.Raw, py = p.Y.Raw - a.Y.Raw;
        var len2 = bx * bx + by * by;
        if (len2 == 0)
        {
            return px * px + py * py <= clear * clear;
        }
        var dot = px * bx + py * by;
        if (dot <= 0 || dot >= len2)
        {
            return false;
        }
        var cross = px * by - py * bx; // distance = |cross| / len; compared squared, in 128 bits' worth of care
        return (Int128)cross * cross <= (Int128)clear * clear * len2;
    }

    // Flood (6.3): from `from` over standable cells (8 neighbours, 10 straight, 14 diagonal, no cutting corners) within `radius`
    // cells (squared, round), settling at most `cap` cells; costs and parents in a (2 radius + 1)^2 box, -1 where unreached.
    private void Flood(Cell from, int radius, int cap)
    {
        var side = 2 * radius + 1;
        var n = side * side;
        if (_floodCost.Length < n)
        {
            (_floodCost, _floodParent) = (new int[n], new int[n]);
        }
        Array.Fill(_floodCost, -1, 0, n);
        Array.Fill(_floodParent, -1, 0, n);
        if (_settledBits.Length * 64 < n)
        {
            _settledBits = new ulong[(n + 63) / 64];
        }
        Array.Clear(_settledBits);
        var settled = new BitBox(_settledBits);
        var heapN = 0;
        var start = radius * side + radius;
        _floodCost[start] = 0;
        Push(ref heapN, start, 0);
        var count = 0;
        while (heapN > 0 && count < cap)
        {
            var top = Pop(ref heapN);
            var i = (int)(top & 0xFFFF);
            var cost = (int)(top >> 16);
            if (settled.Has(i) || cost != _floodCost[i])
            {
                continue;
            }
            settled.Set(i);
            count++;
            int x = i % side, y = i / side;
            for (var s = 0; s < 8; s++)
            {
                var (sx, sy) = SideSteps[s];
                int nx = x + sx, ny = y + sy;
                if (nx < 0 || ny < 0 || nx >= side || ny >= side || (nx - radius) * (nx - radius) + (ny - radius) * (ny - radius) > radius * radius)
                {
                    continue;
                }
                var cell = new Cell(from.X - radius + nx, from.Y - radius + ny);
                if (!Standable(cell) || (sx != 0 && sy != 0 && (!Standable(new Cell(cell.X - sx, cell.Y)) || !Standable(new Cell(cell.X, cell.Y - sy)))))
                {
                    continue;
                }
                var ni = ny * side + nx;
                var nc = cost + (sx != 0 && sy != 0 ? 14 : 10);
                if (settled.Has(ni) || (_floodCost[ni] >= 0 && _floodCost[ni] <= nc))
                {
                    continue;
                }
                (_floodCost[ni], _floodParent[ni]) = (nc, i);
                Push(ref heapN, ni, nc);
            }
        }
        for (var i = 0; i < n; i++)
        {
            if (!settled.Has(i))
            {
                _floodCost[i] = -1; // reached but not settled under the cap: not a candidate
            }
        }
    }

    private bool Standable(Cell c) => Grid.Contains(c) && Grid.Passable(c) && !InWater(c);

    // A min-heap of (cost << 16 | index): ties by the lower index.
    private void Push(ref int n, int i, int cost)
    {
        if (n == _heap.Length)
        {
            Array.Resize(ref _heap, n * 2);
        }
        var v = ((long)cost << 16) | (uint)i;
        var k = n++;
        while (k > 0 && _heap[(k - 1) / 2] > v)
        {
            _heap[k] = _heap[(k - 1) / 2];
            k = (k - 1) / 2;
        }
        _heap[k] = v;
    }

    private long Pop(ref int n)
    {
        var top = _heap[0];
        var v = _heap[--n];
        var k = 0;
        while (true)
        {
            var c = 2 * k + 1;
            if (c >= n)
            {
                break;
            }
            if (c + 1 < n && _heap[c + 1] < _heap[c])
            {
                c++;
            }
            if (_heap[c] >= v)
            {
                break;
            }
            _heap[k] = _heap[c];
            k = c;
        }
        if (n > 0)
        {
            _heap[k] = v;
        }
        return top;
    }

    // BitBox: a small bit set on the stack's worth of words (the flood box holds at most 17 x 17 = 289 cells: 5 words).
    private readonly struct BitBox(ulong[] w)
    {
        private readonly ulong[] _w = w;

        public readonly bool Has(int i) => ((_w[i >> 6] >> (i & 63)) & 1) != 0;

        public readonly void Set(int i) => _w[i >> 6] |= 1UL << (i & 63);
    }

    // ---- Friendly vehicles (5.5, 7.6) ----

    // VehicleCover: a friendly ground vehicle (coverHighTags: high, coverLowTags: low) stopped or under speedPercent of its top speed,
    // within `scan` of c and lying toward direction d (within 2 steps): the soldier at c has it between him and the threat.
    internal CoverKind VehicleCover(Cell c, int d, byte owner, InfantryCombatRules r)
    {
        if (d < 0 || owner >= 64 || (_coverVehicleOwners & (1UL << owner)) == 0)
        {
            return CoverKind.None;
        }
        var p = c.Center();
        var reach = (long)r[CombatValue.VehicleCoverScan] * Fixed.OneRaw / 10;
        var best = CoverKind.None;
        foreach (var v in Around(p.X.Raw - reach, p.Y.Raw - reach, p.X.Raw + reach, p.Y.Raw + reach, _nearThink, true))
        {
            if (v.Owner != owner || !v.Alive || v.Life is not null || v.Def.IsStructure || v.Def.Layer != Layers.Ground || v.Inside != 0 ||
                (v.Def.Tags.Bits & (r.CoverHighTags.Bits | r.CoverLowTags.Bits)) == 0 ||
                v.DriveSpeed.Raw * 100 > v.Def.Speed.Raw * r[CombatValue.VehicleCoverSpeedPercent])
            {
                continue;
            }
            long dx = v.Pos.X.Raw - p.X.Raw, dy = v.Pos.Y.Raw - p.Y.Raw;
            if (dx * dx + dy * dy > reach * reach || DirSteps(Dir32(dx, dy), d) > 2)
            {
                continue;
            }
            var k = (v.Def.Tags.Bits & r.CoverHighTags.Bits) != 0 ? CoverKind.High : CoverKind.Low;
            best = k == CoverKind.High || best == CoverKind.None ? k : best;
        }
        return best;
    }

    // VehicleProtection: a friendly vehicle's cover for a soldier at c in posture p against a threat from d, at its firing or ducked
    // value (low cover keeps lowStanding of it for a standing man), as 5.2 does for the ground's.
    private int VehicleProtection(Cell c, Posture p, int d, bool ducked, byte owner, InfantryCombatRules r)
    {
        var k = VehicleCover(c, d, owner, r);
        var v = CoverValue(k, ducked, r);
        return k == CoverKind.Low && p == Posture.Stand ? v * r[CombatValue.LowStanding] / 100 : v;
    }

    // NoteCoverVehicles (each RunMinds): the players with a ground vehicle that can be cover (a quick test before any vehicle query).
    private void NoteCoverVehicles(InfantryCombatRules r)
    {
        _coverVehicleOwners = 0;
        var tags = r.CoverHighTags.Bits | r.CoverLowTags.Bits;
        foreach (var v in _units)
        {
            if (v.Alive && v.Life is null && v.Owner < 64 && !v.Def.IsStructure && v.Def.Layer == Layers.Ground && (v.Def.Tags.Bits & tags) != 0)
            {
                _coverVehicleOwners |= 1UL << v.Owner;
            }
        }
    }

    // ThinkVehicle (7.6): in contact, within followReach of a friendly ground vehicle moving under speedPercent of its top speed whose
    // goal lies within goalReach of his own, he keeps a slot on its side away from the threat, slotOffset off its hull: slotsHigh
    // behind a high one, slotsLow behind a low one, in ID order; out of contact, or the vehicle gone or too fast, he leaves it.
    private void ThinkVehicle(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        var axis = f.Axis >= 0 ? f.Axis : u.Squad != 0 && Squad(u.Squad) is { Mind.Axis: >= 0 and var a } ? a : -1;
        if (!f.InContact || axis < 0 || u.Def.Speed.Raw == 0 || u.Life is { Busy: true } || u.Order.Kind is not (OrderKind.Move or OrderKind.AttackMove))
        {
            f.Vehicle = 0;
            return;
        }
        var reach = (long)r[CombatValue.VehicleCoverFollowReach] * Fixed.OneRaw / 10;
        var goalReach = (long)r[CombatValue.VehicleCoverGoalReach];
        Unit? best = null;
        foreach (var v in AroundFrom(u, reach, _nearThink, true))
        {
            if (v.Owner != u.Owner || !v.Alive || v.Life is not null || v.Def.IsStructure || v.Def.Layer != Layers.Ground || v.Inside != 0 ||
                (v.Def.Tags.Bits & (r.CoverHighTags.Bits | r.CoverLowTags.Bits)) == 0 || !v.Moving ||
                v.DriveSpeed.Raw * 100 > v.Def.Speed.Raw * r[CombatValue.VehicleCoverSpeedPercent] || (v.Pos - u.Pos).LenSq() > reach * reach)
            {
                continue;
            }
            long gx = v.Order.Cell.X - u.Order.Cell.X, gy = v.Order.Cell.Y - u.Order.Cell.Y;
            if (gx * gx + gy * gy > goalReach * goalReach / 100)
            {
                continue;
            }
            best ??= v; // the nearest is not needed: the lowest ID (the query is in ID order)
        }
        if (best is null)
        {
            f.Vehicle = 0;
            return;
        }
        var slots = (best.Def.Tags.Bits & r.CoverHighTags.Bits) != 0 ? r[CombatValue.VehicleCoverSlotsHigh] : r[CombatValue.VehicleCoverSlotsLow];
        var slot = 0;
        foreach (var o in _units)
        {
            if (o.Id >= u.Id)
            {
                break;
            }
            slot += o.Fight is { Vehicle: var ov } && ov == best.Id ? 1 : 0;
        }
        if (slot >= slots)
        {
            f.Vehicle = 0;
            return;
        }
        (f.Vehicle, f.VehicleSlot) = (best.Id, slot);
        (f.HasSpot, f.SpotMoving) = (false, false);
        var back = Directions[(axis + 16) & 31];
        var across = Directions[(axis + 8) & 31];
        var off = (long)(Fixed.OneRaw + (long)r[CombatValue.VehicleCoverSlotOffset] * Fixed.OneRaw / 10);
        var lateral = (slot == 0 ? 0 : slot == 1 ? 1 : -1) * Fixed.OneRaw;
        var at = new Vec(Fixed.FromRaw(best.Pos.X.Raw + back.X * off / 1024 + across.X * lateral / 1024),
            Fixed.FromRaw(best.Pos.Y.Raw + back.Y * off / 1024 + across.Y * lateral / 1024));
        var cell = Cell.Of(at);
        if (Standable(cell) && cell != Cell.Of(u.Pos) && u.Dest != cell)
        {
            RequestRoute(u, cell);
        }
    }

    // ---- Grenades (14.1 to 14.3) ----

    // GrenadesFor: the grenades a soldier carries (grenadeCarry by archetype; its LMG gunner lmgCarry).
    private static int GrenadesFor(Unit u, InfantryCombatRules r)
    {
        foreach (var (a, n) in r.GrenadeCarry)
        {
            if (a == u.Def.Archetype.Id)
            {
                return u.Def.Weapons.Count > 0 && r.Weapon(u.Def.Weapons[0].Effect) is { Class: CombatClass.Lmg } ? r[CombatValue.GrenadeLmgCarry] : n;
            }
        }
        return 0;
    }

    // ThinkThrow (14.1, 14.2): an enemy seen in the last seenTicks within minRange to maxRange (prone: proneRange) in hard cover facing
    // him (his hit chance on it under hitBelow); no friend within friendClear of the aim point; not pinned hard; the squad's
    // cooldown on that enemy past. Committed: throwTicks to throw (no fire, no move), then it flies (ReleaseGrenade).
    private void ThinkThrow(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        if (f.Grenades <= 0 || f.ThrowAt != 0 || f.Suppression >= r[CombatValue.PinnedHard] || f.PostureLeft > 0 || u.Life is { Busy: true } ||
            u.Def.Weapons.Count == 0)
        {
            return;
        }
        var sq = u.Squad != 0 ? Squad(u.Squad) : null;
        if (sq is null && f.LastThrow != 0 && Tick - f.LastThrow < (uint)r[CombatValue.GrenadeSquadCooldown])
        {
            return; // a loose soldier keeps the squad's cooldown for himself
        }
        var lo = (long)r[CombatValue.GrenadeMinRange] * Fixed.OneRaw / 10;
        var hi = (long)(f.Posture == Posture.Prone ? r[CombatValue.GrenadeProneRange] : r[CombatValue.GrenadeMaxRange]) * Fixed.OneRaw / 10;
        foreach (var k in f.ContactList)
        {
            if (k.Enemy == 0 || k.KnownAt > Tick || k.Source is not (ContactSource.SeenNow or ContactSource.SeenBefore) ||
                Tick - k.Tick > (uint)r[CombatValue.GrenadeSeenTicks] || Unit(k.Enemy) is not { Alive: true } e || e.Inside != 0 ||
                e.Life is { Wound: WoundState.Down })
            {
                continue;
            }
            var aim = k.At.Center();
            var d2 = (aim - u.Pos).LenSq();
            if (d2 < lo * lo || d2 > hi * hi)
            {
                continue;
            }
            var toMe = Dir32(u.Pos.X.Raw - aim.X.Raw, u.Pos.Y.Raw - aim.Y.Raw);
            var hard = CoverToward(k.At, toMe, r) is CoverKind.Low or CoverKind.High || VehicleCover(k.At, toMe, e.Owner, r) != CoverKind.None;
            if (!hard || u.Def.Weapons[0].Effect.Accuracy > 0 && HitChance(u, e, u.Def.Weapons[0]) >= r[CombatValue.GrenadeHitBelow])
            {
                continue;
            }
            if (sq is not null && Cooling(sq, e.Id, r) || FriendNear(u.Owner, aim, (long)r[CombatValue.GrenadeFriendClear] * Fixed.OneRaw / 10))
            {
                continue;
            }
            f.ThrowAt = Tick + (uint)r[CombatValue.GrenadeThrowTicks];
            (f.ThrowTo, f.LastThrow) = (aim, Tick);
            f.Grenades--;
            u.AutoTarget = 0;
            if (sq?.Mind is { } m)
            {
                var oldest = 0;
                for (var i = 1; i < m.Thrown.Length; i++)
                {
                    oldest = m.Thrown[i].Tick < m.Thrown[oldest].Tick ? i : oldest;
                }
                m.Thrown[oldest] = (e.Id, Tick);
            }
            ThinkStats.Throws++;
            return;
        }
    }

    private bool Cooling(Squad s, uint target, InfantryCombatRules r)
    {
        if (s.Mind is not { } m)
        {
            return false;
        }
        foreach (var (t, k) in m.Thrown)
        {
            if (t == target && k != 0 && Tick - k < (uint)r[CombatValue.GrenadeSquadCooldown])
            {
                return true;
            }
        }
        return false;
    }

    private bool FriendNear(byte owner, Vec at, long reach)
    {
        foreach (var o in Around(at.X.Raw - reach, at.Y.Raw - reach, at.X.Raw + reach, at.Y.Raw + reach, _nearThink, true))
        {
            if (o.Owner == owner && o.Alive && o.Inside == 0 && (o.Pos - at).LenSq() <= reach * reach)
            {
                return true;
            }
        }
        return false;
    }

    // ReleaseGrenade: it leaves his hand: lands within scatter of the aim (a recruit scatterRecruit, an elite scatterElite; seeded), a
    // blocked cell moved to the nearest free one toward him, after flightTicks; it goes off fuseTicks after release.
    private void ReleaseGrenade(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        f.ThrowAt = 0;
        var spread = u.Life?.Rank switch
        {
            SoldierRank.Recruit => r[CombatValue.GrenadeScatterRecruit],
            SoldierRank.Elite => r[CombatValue.GrenadeScatterElite],
            _ => r[CombatValue.GrenadeScatter],
        };
        var span = (long)spread * Fixed.OneRaw / 10;
        var hsh = Rand.Hash(Match.Setup.Seed, Tick, u.Id, GrenadeSalt);
        var dx = (long)(hsh % (ulong)(2 * span + 1)) - span;
        var dy = (long)((hsh >> 32) % (ulong)(2 * span + 1)) - span;
        var at = new Vec(Fixed.FromRaw(f.ThrowTo.X.Raw + dx), Fixed.FromRaw(f.ThrowTo.Y.Raw + dy));
        for (var i = 0; i < 8 && !Standable(Cell.Of(at)); i++)
        {
            var back = u.Pos - at;
            at = new Vec(Fixed.FromRaw(at.X.Raw + back.X.Raw / 4), Fixed.FromRaw(at.Y.Raw + back.Y.Raw / 4));
        }
        _grenades.Add(new Grenade(at, u.Id, u.Owner, Tick + (uint)r[CombatValue.GrenadeFlightTicks], Tick + (uint)r[CombatValue.GrenadeFuseTicks]));
    }

    // TickGrenades: a grenade landing is seen (within a soldier's vision, his side seeing that cell) or heard (within hearReach) by
    // the soldiers round it: each reacts after reflexMin to reflexMax ticks (an urgent think), and a zone of `zone` within zoneRadius
    // marks it; going off: 45 at the centre falling to 0 at its radius through the explosive warhead, posture and cover applying.
    private void TickGrenades(InfantryCombatRules r)
    {
        NoteCoverVehicles(r);
        for (var gi = 0; gi < _grenades.Count; gi++)
        {
            var g = _grenades[gi];
            if (Tick == g.LandsAt)
            {
                var zr = (long)r[CombatValue.GrenadeZoneRadius] * Fixed.OneRaw / 10;
                _zones.Add(new DangerZone(Cell.Of(g.At), (int)zr, r[CombatValue.GrenadeZone], 0, 0, g.GoesOff));
                var hear = (long)r[CombatValue.GrenadeHearReach] * Fixed.OneRaw / 10;
                var look = Math.Max(hear, 12L * Fixed.OneRaw);
                foreach (var s in Around(g.At.X.Raw - look, g.At.Y.Raw - look, g.At.X.Raw + look, g.At.Y.Raw + look, _nearThink, true))
                {
                    if (s.Fight is not { } sf || !s.Alive || s.Inside != 0 || s.Garrison != 0 || sf.ReflexAt != 0 || sf.Reflex)
                    {
                        continue;
                    }
                    var d2 = (s.Pos - g.At).LenSq();
                    if (d2 > hear * hear && (d2 > s.Def.Vision.Raw * s.Def.Vision.Raw || !FireLineFrom(Cell.Of(s.Pos), g.At)))
                    {
                        continue;
                    }
                    (sf.ReflexAt, sf.ReflexFrom, sf.Urgent) = (Tick + (uint)Draw(r[CombatValue.GrenadeReflexMin], r[CombatValue.GrenadeReflexMax], s.Id, ReflexSalt), g.At, true);
                }
            }
            if (Tick >= g.GoesOff)
            {
                _grenades.RemoveAt(gi--);
                if (Content.Warhead("explosive") is { } w)
                {
                    Explode(g.At, r[CombatValue.GrenadeDamage], w, Fixed.FromRaw((long)r[CombatValue.GrenadeRadius] * Fixed.OneRaw / 10), edge: 0);
                }
            }
        }
    }

    private bool NoGrenadeNear(Vec at)
    {
        foreach (var g in _grenades)
        {
            if (g.At == at)
            {
                return false;
            }
        }
        return true;
    }

    // StartReflex (14.3): inside, or pinned hard: he stays down; if a run (the fastest way, the back rule's reflex) gets him escape
    // from it before it goes off, he dashes up to `dash` cells, the farthest standable cell (one with cover from the blast first);
    // else he drops prone.
    private void StartReflex(Unit u, SoldierFight f, InfantryCombatRules r)
    {
        f.ReflexAt = 0;
        ThinkStats.Reflexes++;
        uint goesOff = 0;
        foreach (var g in _grenades)
        {
            goesOff = g.At == f.ReflexFrom ? g.GoesOff : goesOff;
        }
        if (goesOff <= Tick || u.Inside != 0 || u.Garrison != 0)
        {
            return;
        }
        var escape = (long)r[CombatValue.GrenadeEscape] * Fixed.OneRaw / 10;
        var now = (u.Pos - f.ReflexFrom).Len().Raw;
        var can = (long)(goesOff - Tick) * u.Def.Speed.Raw * r[CombatValue.GaitRun] / 100;
        if (now >= escape)
        {
            return; // out of it already
        }
        if (f.Suppression < r[CombatValue.PinnedHard] && f.Posture != Posture.Prone && now + can >= escape && u.Def.Speed.Raw > 0)
        {
            var me = Cell.Of(u.Pos);
            var dash = Math.Max(1, r[CombatValue.GrenadeDash] / 10);
            Cell? best = null;
            long bestKey = long.MinValue;
            for (var dy = -dash; dy <= dash; dy++)
            {
                for (var dx = -dash; dx <= dash; dx++)
                {
                    var c = new Cell(me.X + dx, me.Y + dy);
                    if (dx * dx + dy * dy > dash * dash || !Standable(c))
                    {
                        continue;
                    }
                    var p = c.Center();
                    var d2 = (p - f.ReflexFrom).LenSq();
                    var toward = Dir32(f.ReflexFrom.X.Raw - p.X.Raw, f.ReflexFrom.Y.Raw - p.Y.Raw);
                    var key = d2 + (CoverToward(c, toward, r) is CoverKind.Low or CoverKind.High ? (long)Fixed.OneRaw * Fixed.OneRaw * 4 : 0);
                    if (key > bestKey)
                    {
                        (best, bestKey) = (c, key);
                    }
                }
            }
            if (best is { } to && to != me)
            {
                f.Reflex = true;
                (f.HasSpot, f.SpotMoving) = (false, false);
                RequestRoute(u, to);
                return;
            }
        }
        ChangePosture(u, f, Posture.Prone, false, true, r);
    }

    // SplashPosture: the share of a blast a soldier takes by his posture (prone proneDamage; brief 6.3's 90% for a crouch or kneel is
    // left at 100 until the brief's table is content).
    internal int SplashPosture(Unit u)
    {
        if (u.Fight is not { } f || Combat is not { } r)
        {
            return 100;
        }
        return f.Posture == Posture.Prone && f.PostureLeft == 0 ? r[CombatValue.GrenadeProneDamage] : 100;
    }

    private void HashThinkWorld(ref WordHash h)
    {
        h.PutSigned(_grenades.Count);
        foreach (var g in _grenades)
        {
            h.Put4(unchecked((ulong)g.At.X.Raw), unchecked((ulong)g.At.Y.Raw), g.Thrower | ((ulong)g.Owner << 32), g.LandsAt | ((ulong)g.GoesOff << 32));
        }
    }
}
