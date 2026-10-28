using System.Numerics;
using System.Runtime.InteropServices;
using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The ambush (docs/design.md, "Squads: Ambush", phase S3). The player drags a kill zone (its start where the enemy comes in, its
// far end where the column is stopped); the squad sets a trap round it and springs it on its own.
//  1. The side. Once per order both sides of the zone are scored with Hold's position picking (SquadHoldSystem.cs), the zone as
//     the threat: the cells within the rules' radius of the zone on that side (the nearest first, capped), each member taking the
//     best one left for his role (the ambush's role weights and limits); the side whose picks score more is taken (a tie: the
//     right). Everyone stays on it. Several squads given one zone take the side the first picked.
//  2. Garrisonable buildings on that side within reach of the zone, with sight of it, are positions for the roles that garrison.
//  3. The line soldiers lay mines from their own stock: anti-tank mines on the road along the zone from the far end back,
//     anti-personnel mines and booby traps on the cover off its far side; one a member at a time, then he takes his position.
//  4. Everyone holds fire (a set or setting ambush forces Hold fire: under trees that also hides them from the air). Setting
//     until everyone is in place with his mines laid (or the rules' set time is up), then Ready.
//  5. It springs on the first of: an enemy vehicle at the far end or a crowd in the zone (seen by the squad's side), a mine in the
//     zone going off, an enemy spotting a member, a member hurt, the player's Fire (or an attack order). Sprung, everyone takes
//     the squad's stance and fights from his spot (RPGs and the Kornet vehicles first, the line infantry first; the mortar the
//     zone first); nobody chases. After the rules' rearm time without a fight it is Ready again; the mines are not laid again.
// Integers only, no random draws, ID order and cell-index tie-breaks.
public sealed partial class World
{
    // The zone of the squad the scratch holds (ZoneSet): its ends, its direction and length, and the side's normal (raw units).
    private Vec _ambA, _ambB, _ambZ, _ambN, _ambApproach;
    private long _ambLen;
    private readonly List<(long D, int Key)> _ambCells = [];
    private readonly List<Unit> _ambNear = [];

    /// <summary>
    /// The mines squad laid in its ambush that are still on the map, in laying order (the client marks them). Empty for no squad or
    /// content without an ambush.
    /// </summary>
    public IReadOnlyList<AmbushMine> SquadAmbushMines(uint squad)
    {
        var res = new List<AmbushMine>();
        if (Squad(squad) is not { } s || Content.Squad?.Ambush is not { } amb)
        {
            return res;
        }
        foreach (var id in s.MineList)
        {
            if (Unit(id) is { Alive: true } m)
            {
                res.Add(new AmbushMine(id, Cell.Of(m.Pos), MineKind(m.Def.Archetype.Index, amb)));
            }
        }
        return res;
    }

    private static AmbushMineKind MineKind(int archetype, SquadAmbushRules amb) =>
        archetype == amb.AtMine ? AmbushMineKind.AntiTank : archetype == amb.ApMine ? AmbushMineKind.AntiPersonnel : AmbushMineKind.Trap;

    // AmbushHoldsFire: the squad is setting or has set an ambush (not sprung): its members hold fire.
    private static bool AmbushHoldsFire(Squad s) => s.Mode == SquadMode.Ambush && s.AmbushPhase is SquadState.Setting or SquadState.Ready;

    // AmbushBusy: the member still has a mine of the ambush to lay.
    private static bool AmbushBusy(Squad s, uint unit)
    {
        foreach (var t in s.TaskList)
        {
            if (t.Unit == unit && t.State != AmbushTaskState.Done)
            {
                return true;
            }
        }
        return false;
    }

    // BenchAmbushPick (benchmarks only): scores both sides of ambushing squad s's zone as its order did (nothing is changed) and
    // returns the cells it looked at.
    internal int BenchAmbushPick(Squad s)
    {
        if (s.Mode != SquadMode.Ambush || Content.Squad is not { Hold: { } hold, Ambush: { } amb })
        {
            return 0;
        }
        long scored = 0;
        var r = SyncRouting();
        AmbushPickSide(r, s, 1, hold, amb, [], ref scored);
        AmbushPickSide(r, s, -1, hold, amb, [], ref scored);
        return (int)scored;
    }

    // ---- The order ----

    // AmbushSquads gives the Ambush order c to squads (ID order): the zone from Cell to Cell2. A squad mostly of Iglas sets an air
    // ambush (phase S4: the zone is an air corridor). Outside the lengths for its kind it is refused (one refusal per squad, for its
    // leader).
    private void AmbushSquads(List<Squad> squads, Command c, SquadRules rules, SquadAmbushRules amb)
    {
        Cell Clamp(Cell x) => new(Math.Clamp(x.X, 0, Grid.Width - 1), Math.Clamp(x.Y, 0, Grid.Height - 1));
        var a = Clamp(c.Cell);
        var b = c.Cell2 is { } e ? Clamp(e) : a;
        var len = (b.Center() - a.Center()).Len().Raw;
        foreach (var s in squads)
        {
            var air = AirAmbushSquad(s, amb);
            var (min, max) = air ? (amb.Air!.MinLength.Raw, amb.Air.MaxLength.Raw) : (amb.MinLength.Raw, amb.MaxLength.Raw);
            if (len < min || len > max || rules.Hold is null)
            {
                Refuse(Unit(s.Leader)!, OrderRefusalReason.ZoneLength, 0, a);
                continue;
            }
            AmbushOrder(s, a, b, rules, air);
        }
    }

    /// <summary>
    /// Whether an Ambush order given to squad now would set an air ambush (phase S4): its members are mostly Iglas (the content's air
    /// part: more than half). The client shows the corridor's limits instead of the kill zone's. False for no squad or no air ambush.
    /// </summary>
    public bool AirAmbushSquad(uint squad) => Squad(squad) is { } s && Content.Squad?.Ambush is { } amb && AirAmbushSquad(s, amb);

    private bool AirAmbushSquad(Squad s, SquadAmbushRules amb)
    {
        if (amb.Air is not { } air)
        {
            return false;
        }
        var n = 0;
        foreach (var id in s.MemberList)
        {
            if (SquadRoleOf(Unit(id)!) == SquadRole.AirGuard)
            {
                n++;
            }
        }
        return air.IsAir(n, s.MemberList.Count);
    }

    // AmbushOrder: s sets an ambush round the zone a to b. Its cells are picked in a squad pass (both sides scored); a squad far from
    // the chosen side marches there first. Everyone holds fire from now until it springs.
    private void AmbushOrder(Squad s, Cell a, Cell b, SquadRules rules, bool air)
    {
        if (s.Loose)
        {
            ReAnchor(s);
            s.Loose = false;
        }
        LeaveAmbush(s);
        LeaveHold(s);
        if (s.Order != OrderKind.Idle)
        {
            EndSquadOrder(s);
        }
        s.Mode = SquadMode.Ambush;
        BuildSlots(s, rules);
        s.AmbushStart = a;
        s.AmbushEnd = b;
        s.AmbushSide = 0;
        s.AmbushSpot = default;
        s.AmbushPhase = SquadState.Setting;
        s.AmbushTrigger = AmbushTrigger.None;
        s.SpringPending = AmbushTrigger.None;
        s.AmbushSince = Tick;
        s.AmbushCalm = 0;
        s.MineList.Clear();
        s.TaskList.Clear();
        s.PickList.Clear();
        s.AltPicks.Clear();
        s.AmbushRightDone = false;
        s.AmbushRightScore = 0;
        s.AmbushAir = air;
        s.VolleyTarget = 0;
        s.VolleySince = 0;
        s.HoldPlaced = false;
        s.HoldPending = true;
        foreach (var id in s.MemberList)
        {
            Unit(id)!.Stance = Stance.HoldFire;
        }
        _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Setting, s.Leader, 0));
    }

    // LeaveAmbush: the squad stops ambushing (a new order or mode): its mines being laid are given up, its hold fire lifted, its
    // garrisons come out and its mortar packs up (LeaveHold). The mines it laid stay (and in its list until the next ambush).
    private void LeaveAmbush(Squad s)
    {
        if (s.Mode != SquadMode.Ambush)
        {
            return;
        }
        LeaveAmbushTasks(s);
        LeaveHold(s);
        s.AltPicks.Clear();
        s.AmbushRightDone = false;
        s.AmbushRightScore = 0;
        s.AmbushPhase = SquadState.Idle;
        s.AmbushSide = 0;
        s.SpringPending = AmbushTrigger.None;
        s.AmbushCalm = 0;
        s.AmbushAir = false;
        s.VolleyTarget = 0;
        s.VolleySince = 0;
    }

    // LeaveAmbushTasks: the mines being laid are given up (their layers stop), the tasks dropped, the hold fire lifted.
    private void LeaveAmbushTasks(Squad s)
    {
        if (s.Mode != SquadMode.Ambush)
        {
            return;
        }
        foreach (var t in s.TaskList)
        {
            if (t.State == AmbushTaskState.Laying && Unit(t.Unit) is { } u && Laying(u, t))
            {
                u.Idle();
            }
        }
        s.TaskList.Clear();
        if (AmbushHoldsFire(s))
        {
            foreach (var id in s.MemberList)
            {
                Unit(id)!.Stance = s.Stance;
            }
        }
    }

    // LeaveAmbushTask: member u leaves s (detached): his mine is given up, the squad's stance his again.
    private void LeaveAmbushTask(Squad s, Unit u)
    {
        if (s.Mode != SquadMode.Ambush)
        {
            return;
        }
        foreach (var t in s.TaskList)
        {
            if (t.Unit == u.Id && t.State == AmbushTaskState.Laying && Laying(u, t))
            {
                u.Idle();
            }
        }
        s.TaskList.RemoveAll(t => t.Unit == u.Id);
        if (AmbushHoldsFire(s))
        {
            u.Stance = s.Stance;
        }
    }

    // Laying: u is on the order that lays task t.
    private static bool Laying(Unit u, AmbushMineTask t) =>
        u.Order.Kind == OrderKind.UseAbility && u.Order.Cell == t.Cell && u.Order.Ability == t.Ability;

    // SpringAmbush (Fire): the listed members' squads that hold fire in an ambush spring it in the next squad pass.
    private void SpringAmbushCommand(Command c)
    {
        foreach (var id in c.Units)
        {
            if (Unit(id) is { Squad: not 0 } u && u.Owner == c.Player && Squad(u.Squad) is { } s && AmbushHoldsFire(s) &&
                s.SpringPending == AmbushTrigger.None)
            {
                s.SpringPending = AmbushTrigger.Order;
            }
        }
    }

    // ---- Hooks ----

    // AmbushMemberHurt (Hurt): member t lost HP. A set or setting ambush springs (next squad pass); a sprung one is not calm.
    private void AmbushMemberHurt(Unit t)
    {
        if (Squad(t.Squad) is not { Mode: SquadMode.Ambush } s || s.SpringPending != AmbushTrigger.None)
        {
            return;
        }
        if (AmbushHoldsFire(s) || s.AmbushPhase == SquadState.Sprung)
        {
            s.SpringPending = AmbushTrigger.Hurt;
        }
    }

    // AmbushTrapOff (GoOff): a trap went off at p: each ambush holding fire with p in its zone springs (next squad pass).
    private void AmbushTrapOff(Vec p)
    {
        if (Content.Squad?.Ambush is not { } amb)
        {
            return;
        }
        foreach (var s in _squads)
        {
            if (!AmbushHoldsFire(s) || s.SpringPending != AmbushTrigger.None || s.AmbushAir)
            {
                continue; // (an air corridor is no zone on the ground)
            }
            ZoneSet(s, 1);
            if (InZone(p, amb.Width.Raw + Fixed.OneRaw))
            {
                s.SpringPending = AmbushTrigger.Mine;
            }
        }
    }

    // AmbushMineLaid (the ability system): a member of an ambushing squad laid trap n: the squad's list and an event.
    private void AmbushMineLaid(Unit n)
    {
        if (Unit(n.Source) is not { Squad: not 0 } src || Squad(src.Squad) is not { Mode: SquadMode.Ambush } s)
        {
            return;
        }
        s.MineList.Add(n.Id);
        _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.MineLaid, src.Id, n.Id));
    }

    // AmbushHolds (the unit AI): u is in an ambush (not on its squad's march there): he never chases, he fights from his spot.
    private bool AmbushHolds(Unit u) => u.Squad != 0 && Squad(u.Squad) is { Mode: SquadMode.Ambush, Order: OrderKind.Idle };

    // AmbushPrefer (target priority; 0 first): in a sprung ambush the RPGs and the Kornet take vehicles first, the line infantry
    // first, the mortar what is in the zone first.
    private long AmbushPrefer(Unit u, Unit t)
    {
        if (Squad(u.Squad) is not { Mode: SquadMode.Ambush, AmbushPhase: SquadState.Sprung } s || Content.Squad?.Ambush is not { } amb)
        {
            return 0;
        }
        var vehicle = t.Def.Tags.Matches(amb.Vehicles);
        switch (SquadRoleOf(u))
        {
            case SquadRole.Flank or SquadRole.LongFlank:
                return vehicle ? 0 : 1;
            case SquadRole.Line:
                return vehicle ? 1 : 0;
            case SquadRole.Support:
                ZoneSet(s, 1);
                return InZone(t.Pos, amb.Width.Raw) ? 0 : 1;
            default:
                return 0;
        }
    }

    // ---- The squad pass ----

    // RunAmbush (the squad pass, for a squad in Ambush): the triggers, the rearm after a fight, the picking (a few a tick), the march
    // to the side, the mines laid, the positions held, and Ready once everyone is in place.
    private void RunAmbush(Router r, Squad s, SquadRules rules)
    {
        if (rules.Hold is not { } hold || rules.Ambush is not { } amb)
        {
            return;
        }
        var settled = s.HoldPlaced && s.Order == OrderKind.Idle;
        if (AmbushHoldsFire(s))
        {
            foreach (var id in s.MemberList)
            {
                Unit(id)!.Stance = Stance.HoldFire; // (one who joined, or a stance given to a member)
            }
            var trig = s.SpringPending;
            if (trig == AmbushTrigger.None && settled)
            {
                trig = s.AmbushAir ? AirTriggered(s, amb.Air!) : AmbushTriggered(s, amb);
            }
            if (trig != AmbushTrigger.None)
            {
                Spring(s, trig);
            }
        }
        else if (s.AmbushPhase == SquadState.Sprung)
        {
            var busy = s.SpringPending != AmbushTrigger.None; // a member hurt
            s.SpringPending = AmbushTrigger.None;
            foreach (var id in s.MemberList)
            {
                var u = Unit(id)!;
                busy |= u.AutoTarget != 0 || u.AirTarget != 0 || u.Ai != AiState.None || u.Order.Kind is OrderKind.Attack or OrderKind.AttackGround;
            }
            busy = busy || (s.AmbushAir ? AirInReach(s, amb.Air!) > 0 : ZoneEnemies(s, amb, out _) > 0);
            s.AmbushCalm = busy ? 0 : s.AmbushCalm + 1;
            if (s.AmbushCalm >= amb.RearmTicks)
            {
                s.AmbushPhase = SquadState.Ready; // set again (the mines are not laid again)
                s.AmbushCalm = 0;
                foreach (var id in s.MemberList)
                {
                    Unit(id)!.Stance = Stance.HoldFire;
                }
                _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Ready, s.Leader, 0));
            }
        }
        if (s.AmbushAir && s.AmbushPhase == SquadState.Sprung)
        {
            AirVolleyTarget(s); // the aircraft its Iglas fire at together (SquadAirAmbushSystem.cs)
        }
        if (s.Loose || s.Order != OrderKind.Idle)
        {
            return;
        }
        if (s.HoldPending)
        {
            var cost = s.AmbushSide == 0 ? 2 : 1; // a side scored (one a tick), or the side's members who joined
            if (_holdPicks > 0 && _holdPicks + cost > HoldPicksPerTick)
            {
                return;
            }
            _holdPicks += cost;
            PickAmbush(r, s, hold, amb);
            return;
        }
        if (!s.HoldPlaced)
        {
            return;
        }
        if (s.AmbushPhase == SquadState.Setting)
        {
            LayMines(s);
        }
        ZoneSet(s, s.AmbushSide);
        HoldMembers(s, ZonePoint(_ambLen / 2));
        if (s.AmbushPhase == SquadState.Setting)
        {
            var late = Tick - s.AmbushSince >= (uint)amb.SetTicks;
            if (late || AmbushInPlace(s))
            {
                if (late)
                {
                    LeaveAmbushLaying(s); // the mines still to lay are given up
                }
                s.AmbushPhase = SquadState.Ready;
                _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Ready, s.Leader, 0));
            }
        }
    }

    // Spring: the ambush springs on trig: everyone takes the squad's stance (and fights from his spot); mines not laid yet are given up.
    private void Spring(Squad s, AmbushTrigger trig)
    {
        s.AmbushPhase = SquadState.Sprung;
        s.VolleyTarget = 0; // (an air ambush picks its target in this squad pass)
        s.VolleySince = 0;
        s.AmbushTrigger = trig;
        s.SpringPending = AmbushTrigger.None;
        s.AmbushCalm = 0;
        LeaveAmbushLaying(s);
        foreach (var id in s.MemberList)
        {
            Unit(id)!.Stance = s.Stance;
        }
        _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Sprung, s.Leader, (uint)trig));
    }

    // LeaveAmbushLaying: every mine task not done is given up (its layer stops).
    private void LeaveAmbushLaying(Squad s)
    {
        var tasks = CollectionsMarshal.AsSpan(s.TaskList);
        for (var i = 0; i < tasks.Length; i++)
        {
            var t = tasks[i];
            if (t.State == AmbushTaskState.Done)
            {
                continue;
            }
            if (t.State == AmbushTaskState.Laying && Unit(t.Unit) is { } u && Laying(u, t))
            {
                u.Idle();
            }
            tasks[i] = t with { State = AmbushTaskState.Done };
        }
    }

    // AmbushTriggered: what springs a placed ambush now, if anything: an enemy vehicle at the far end, a crowd in the zone (both seen
    // by the squad's side), an enemy spotting a member.
    private AmbushTrigger AmbushTriggered(Squad s, SquadAmbushRules amb)
    {
        var n = ZoneEnemies(s, amb, out var far);
        if (far)
        {
            return AmbushTrigger.FarEnd;
        }
        if (n >= amb.Crowd)
        {
            return AmbushTrigger.Crowd;
        }
        var enemies = ~(Bit(s.Owner) | Bit(0));
        foreach (var id in s.MemberList)
        {
            if (Unit(id) is { Inside: 0, Garrison: 0 } u && (u.SpottedBy & enemies) != 0)
            {
                return AmbushTrigger.Spotted;
            }
        }
        return AmbushTrigger.None;
    }

    // ZoneEnemies counts the enemy ground units in s's zone that its side spots (no structures or traps); far: one of them is a
    // vehicle within the rules' far-end distance of the far end.
    private int ZoneEnemies(Squad s, SquadAmbushRules amb, out bool far)
    {
        ZoneSet(s, 1);
        far = false;
        var w = amb.Width.Raw;
        var m = w + Fixed.OneRaw;
        long x0 = Math.Min(_ambA.X.Raw, _ambB.X.Raw) - m, x1 = Math.Max(_ambA.X.Raw, _ambB.X.Raw) + m;
        long y0 = Math.Min(_ambA.Y.Raw, _ambB.Y.Raw) - m, y1 = Math.Max(_ambA.Y.Raw, _ambB.Y.Raw) + m;
        var n = 0;
        foreach (var o in Around(x0, y0, x1, y1, _ambNear, false)) // a count and a flag: the order does not matter
        {
            if (!o.Alive || o.Owner == s.Owner || o.Owner == 0 || o.Inside != 0 || o.Garrison != 0 || o.Def.Layer != Layers.Ground ||
                o.Def.IsStructure || o.Def.Trigger is not null || !InZone(o.Pos, w) || !IsSpotted(o, s.Owner))
            {
                continue;
            }
            n++;
            if (o.Def.Tags.Matches(amb.Vehicles) && ZoneAx(o.Pos) >= _ambLen - amb.FarEnd.Raw)
            {
                far = true;
            }
        }
        return n;
    }

    // AmbushInPlace: every member is at his position (in his building, or on his cell and not moving), his mines laid, a set-up
    // weapon set up.
    private bool AmbushInPlace(Squad s)
    {
        foreach (var p in s.PickList)
        {
            if (Unit(p.Unit) is not { Alive: true } u)
            {
                continue;
            }
            if (AmbushBusy(s, u.Id))
            {
                return false;
            }
            if (p.Building != 0)
            {
                if (u.Garrison != p.Building)
                {
                    return false;
                }
                continue;
            }
            if (u.Inside != 0 || u.Garrison != 0 || u.Moving || !Within(u.Pos, p.Cell.Center(), Fixed.One) ||
                (u.Def.Archetype.DeployTicks > 0 && u.Deploy != DeployState.Deployed))
            {
                return false;
            }
        }
        return true;
    }

    // LayMines: each member with a mine left to lay lays his next one: a member on the map and idle is sent (a UseAbility order:
    // he walks there and lays it); one whose order ended is done with it (laid or not: the ability system lays it or gives up).
    private void LayMines(Squad s)
    {
        var tasks = CollectionsMarshal.AsSpan(s.TaskList);
        for (var i = 0; i < tasks.Length; i++)
        {
            var t = tasks[i];
            if (t.State == AmbushTaskState.Done || (i > 0 && tasks[i - 1].Unit == t.Unit && tasks[i - 1].State != AmbushTaskState.Done))
            {
                continue; // one at a time: his earlier one first
            }
            if (Unit(t.Unit) is not { Alive: true } u || u.Squad != s.Id)
            {
                tasks[i] = t with { State = AmbushTaskState.Done };
                continue;
            }
            if (t.State == AmbushTaskState.Laying)
            {
                if (!Laying(u, t))
                {
                    tasks[i] = t with { State = AmbushTaskState.Done };
                }
                continue;
            }
            if (u.Garrison != 0 && Unit(u.Garrison) is { } b && u.Order.Kind == OrderKind.Idle)
            {
                LeaveBuilding(u, b, NearestDoor(b, t.Cell.Center()), 0); // out first: he lays it, then takes his position
                continue;
            }
            if (u.Inside != 0 || u.Garrison != 0 || !u.CanAct || u.Order.Kind != OrderKind.Idle || u.Ai != AiState.None)
            {
                continue;
            }
            if (u.Def.Ability(t.Ability) is not { } ab || u.Charges[ab.Stock] == 0)
            {
                tasks[i] = t with { State = AmbushTaskState.Done }; // none left: he just takes his spot
                continue;
            }
            Assign(u, new Order(OrderKind.UseAbility, t.Cell, 0, t.Ability));
            tasks[i] = t with { State = AmbushTaskState.Laying };
        }
    }

    // ---- Picking ----

    // PickAmbush picks the members' positions once: the first time both sides are scored, the right in one squad pass and the left
    // in the next, and the better one is taken (a tie: the right; or the side another of the player's squads took on the same zone),
    // its mines planned; later (members who joined) only the side's. A squad farther than the radius from the side's spot marches
    // there first.
    private void PickAmbush(Router r, Squad s, SquadHoldRules hold, SquadAmbushRules amb)
    {
        if (s.AmbushAir && amb.Air is { } air)
        {
            PickAirAmbush(r, s, hold, amb, air); // SquadAirAmbushSystem.cs
            return;
        }
        s.HoldPending = false;
        long scored = 0;
        List<HoldPick> picks;
        var first = s.AmbushSide == 0;
        if (first)
        {
            var side = SharedSide(s);
            if (side == 0 && !s.AmbushRightDone)
            {
                // The right side this pass, the left the next (one side a tick): the right's picks and worth wait in the squad.
                s.AltPicks.Clear();
                s.AmbushRightScore = AmbushPickSide(r, s, 1, hold, amb, s.AltPicks, ref scored);
                s.AmbushRightDone = true;
                s.HoldPending = true;
                return;
            }
            if (side == 0)
            {
                var left = new List<HoldPick>();
                var tl = AmbushPickSide(r, s, -1, hold, amb, left, ref scored);
                (side, picks) = tl > s.AmbushRightScore ? (-1, left) : (1, new List<HoldPick>(s.AltPicks));
            }
            else
            {
                picks = new List<HoldPick>(s.PickList);
                AmbushPickSide(r, s, side, hold, amb, picks, ref scored);
            }
            s.AmbushSide = side;
            ZoneSet(s, side);
            var mid = ZonePoint(_ambLen / 2);
            var spot = Cell.Of(mid + Scale(_ambN, amb.Spot));
            s.AmbushSpot = new Cell(Math.Clamp(spot.X, 0, Grid.Width - 1), Math.Clamp(spot.Y, 0, Grid.Height - 1));
            s.HoldSpot = s.AmbushSpot;
            s.HoldFacing = new Vec(-_ambN.X, -_ambN.Y);
            s.HoldThreat = mid;
            s.HoldFaced = true;
        }
        else
        {
            picks = new List<HoldPick>(s.PickList);
            AmbushPickSide(r, s, s.AmbushSide, hold, amb, picks, ref scored);
        }
        s.AltPicks.Clear();
        s.AmbushRightDone = false;
        s.AmbushRightScore = 0;
        s.PickList.Clear();
        s.PickList.AddRange(picks);
        s.PickList.RemoveAll(p => s.MemberList.BinarySearch(p.Unit) < 0); // (one lost since the right side was scored)
        foreach (var id in s.MemberList)
        {
            if (HoldPickIndex(s, id) < 0)
            {
                s.HoldPending = true; // one who joined since: he picks next
            }
        }
        foreach (var p in s.PickList)
        {
            if (Unit(p.Unit) is { Garrison: not 0 } u && u.Garrison != p.Building && Unit(u.Garrison) is { } other)
            {
                LeaveBuilding(u, other, NearestDoor(other, p.Cell.Center()), 0); // in another building: out, to his position
            }
        }
        if (first)
        {
            PlanAmbushMines(s, hold, amb);
        }
        s.HoldPlaced = true;
        if (Within(s.Anchor, s.AmbushSpot.Center(), amb.Radius))
        {
            Settle(s, s.AmbushSpot.Center(), s.HoldFacing);
        }
        else
        {
            var listed = new List<Unit>();
            foreach (var id in s.MemberList)
            {
                if (Unit(id) is { Inside: 0, Garrison: 0 } m)
                {
                    listed.Add(m);
                }
            }
            OrderSquad(s, s.AmbushSpot, OrderKind.Move, listed); // it marches to the side first (as March), then takes its positions
        }
        _squadEvents.Add(new SquadEvent(s.Id, s.Owner, SquadEventKind.Placed, s.Leader, (uint)scored));
    }

    // SharedSide is the side another of the owner's squads ambushing the same zone took, or 0.
    private int SharedSide(Squad s)
    {
        foreach (var o in _squads)
        {
            if (o != s && o.Owner == s.Owner && o.Mode == SquadMode.Ambush && o.AmbushSide != 0 && o.AmbushStart == s.AmbushStart &&
                o.AmbushEnd == s.AmbushEnd)
            {
                return o.AmbushSide;
            }
        }
        return 0;
    }

    // AmbushPickSide scores s's candidates on one side of its zone and gives each member without a position in `into` the best one
    // left for his role (Hold's pick order, ID order within a role; ties to the lowest cell index, buildings after cells by ID).
    // Returns the sum of the scores picked (the side's worth); scored gains the cells looked at.
    private long AmbushPickSide(Router r, Squad s, int side, SquadHoldRules hold, SquadAmbushRules amb, List<HoldPick> into, ref long scored)
    {
        var looked = AmbushCandidates(r, s, side, hold, amb);
        scored += looked;
        HoldCellsScored += looked;
        _holdLanes.Clear();
        HoldTaken(s, 0, _holdSc, amb.Radius.Raw + _ambLen / 2);
        var n = _holdCands.Count;
        if (_holdDmin.Length < n)
        {
            _holdDmin = new long[n];
            _holdDsame = new long[RoleCount * n];
        }
        Array.Fill(_holdDmin, long.MaxValue, 0, n);
        Array.Fill(_holdDsame, long.MaxValue, 0, RoleCount * n);
        foreach (var (at, taker, _) in _holdTaken)
        {
            TakeDist(at, taker, n);
        }
        long total = 0;
        foreach (var role in hold.Order)
        {
            var ar = amb.Roles[(int)role];
            var ends = 0;
            foreach (var id in s.MemberList)
            {
                var u = Unit(id)!;
                if (SquadRoleOf(u) != role)
                {
                    continue;
                }
                var endAx = ar.End switch
                {
                    AmbushEnd.Far => _ambLen,
                    AmbushEnd.Near => 0,
                    AmbushEnd.Both => ends++ % 2 == 0 ? _ambLen : 0, // the lead vehicle's end first, then the tail's
                    _ => -1,
                };
                if (PickIndexIn(into, id) >= 0)
                {
                    continue;
                }
                // The tail's end (the zone's start, where the column comes in) is met first: an RPG there keeps to the far half of his
                // band off the zone's line, deeper in his cover, so the column passes him before it sees him (SQ-FIX, decision 694).
                var latMin = ar.End == AmbushEnd.Both && endAx == 0 && ar.LateralMax.Raw > 0
                    ? (ar.LateralMin.Raw + ar.LateralMax.Raw) / 2 : ar.LateralMin.Raw;
                HoldMember(u, out var reach, out var minR, out var lane);
                var back = LineDepth(into, 0);
                var col = SightColumn(lane);
                var best = -1;
                var bestScore = 0;
                for (var pass = 0; pass < 2 && best < 0; pass++)
                {
                    var cands = CollectionsMarshal.AsSpan(_holdCands);
                    for (var i = 0; i < cands.Length; i++)
                    {
                        ref readonly var c = ref cands[i];
                        if (c.Building != 0 && c.Room <= 0)
                        {
                            continue;
                        }
                        var sight = 0;
                        if (ar.Weights.Hold.Sight != 0)
                        {
                            ref var memo = ref _holdSight[col * cands.Length + i];
                            if (memo < 0)
                            {
                                memo = Sight(c, lane);
                            }
                            sight = memo;
                        }
                        if ((ar.Weights.Lane | ar.Weights.Approach) != 0 && c.Steps3 < 0)
                        {
                            ApproachLaneOf(ref cands[i]); // worked out once, for the roles that weigh it
                        }
                        var (score, ok) = AmbushScore(u, ar, c, reach, minR, lane, back, strict: pass == 0, _holdDmin[i],
                            _holdDsame[(int)role * cands.Length + i], sight, endAx, amb.Radius.Raw, latMin);
                        if (ok && (best < 0 || score > bestScore || (score == bestScore && c.Key < cands[best].Key)))
                        {
                            (best, bestScore) = (i, score);
                        }
                    }
                }
                HoldPick pick;
                if (best < 0)
                {
                    pick = new HoldPick(id, Cell.Of(u.Pos), 0); // nothing left: he holds where he stands
                }
                else
                {
                    total += bestScore;
                    var c = _holdCands[best];
                    if (c.Building != 0)
                    {
                        c.Room--;
                        _holdCands[best] = c;
                        var b = Unit(c.Building)!;
                        pick = new HoldPick(id, DoorCell(b, NearestDoor(b, c.P)), c.Building);
                    }
                    else
                    {
                        pick = new HoldPick(id, c.C, 0);
                    }
                }
                into.Insert(~into.BinarySearch(pick, PickById), pick);
                if (pick.Building == 0)
                {
                    TakeDist(pick.Cell.Center(), role, n);
                }
            }
        }
        return total;
    }

    // PickIndexIn is the index of unit's pick in picks (sorted by unit), or -1.
    private static int PickIndexIn(List<HoldPick> picks, uint unit)
    {
        int lo = 0, hi = picks.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (picks[mid].Unit < unit)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo < picks.Count && picks[lo].Unit == unit ? lo : -1;
    }

    // AmbushScore scores candidate c for member u in the ambush (his role's habits ar), as HoldScore does, with the ambush's limits
    // (strict: the distance from the zone's line, at least latMin, out of its sight for a role that hides) and factors (his end of the zone at
    // endAx, -1 for none; the lane to the approach and the approach point seen; out of the zone's sight). ok false: ruled out.
    private (int Score, bool Ok) AmbushScore(Unit u, SquadAmbushRole ar, in HoldCand c, long reach, long minR, long lane, long backFrom,
        bool strict, long dmin, long dsame, int sight, long endAx, long radius, long latMin)
    {
        var building = c.Building != 0;
        bool ok;
        if (building)
        {
            var b = Unit(c.Building)!;
            ok = ar.Garrison && reach > 0 && c.DistT <= reach + c.Range && u.Def.Tags.Matches(b.Def.Archetype.Garrison!.Tags);
        }
        else
        {
            ok = dmin >= (long)Fixed.OneRaw * Fixed.OneRaw; // someone holds this cell
            if (ok && strict)
            {
                ok = dmin >= ar.Apart.Raw * ar.Apart.Raw && dsame >= ar.ApartSame.Raw * ar.ApartSame.Raw &&
                    (latMin == 0 || c.Lat >= latMin) && (ar.LateralMax.Raw == 0 || c.Lat <= ar.LateralMax.Raw);
            }
            ok = ok && SetsUpOn(u, c.C); // the mortar never takes a cell it can't set up on (SquadHoldSystem.cs)
        }
        if (strict && ok && (Depth(c) < backFrom + ar.Back.Raw || (ar.Hide && c.Hidden < 100)))
        {
            ok = false;
        }
        if (!ok)
        {
            return (0, false);
        }
        var spacing = 100;
        if (!building && dmin < ar.Spread.Raw * ar.Spread.Raw)
        {
            spacing = (int)(Fixed.Sqrt(dmin) * 100 / ar.Spread.Raw);
        }
        var range = 0;
        var w = reach + c.Range;
        if (reach > 0 && c.DistT <= w && c.DistT >= minR)
        {
            var best = w * 3 / 4;
            range = (int)Math.Max(0, 100 - Math.Abs(c.DistT - best) * 100 / Math.Max(1, best));
        }
        var end = endAx < 0 ? 0 : (int)Math.Max(0, 100 - Math.Abs(c.Ax - endAx) * 100 / radius);
        var (laneSeen, approach) = ApproachLane(c, lane);
        var k = ar.Weights;
        var h = k.Hold;
        var score = h.Cover * c.Cover + h.Trees * c.Trees + h.Sight * sight + h.Open * c.Open + h.Spacing * spacing + h.Safety * c.Safety +
            h.Range * range + h.Near * c.Near + k.End * end + k.Lane * laneSeen + k.Approach * approach + k.Hidden * c.Hidden;
        return (score, true);
    }

    // ApproachLane scores a candidate's lane to the approach for a member whose own lanes reach `lane` (raw): how much of it out to
    // his reach he sees (0 to 100), and whether he sees the approach point itself within it (100 or 0).
    private (int Lane, int Approach) ApproachLane(in HoldCand c, long lane)
    {
        if (c.Steps3 <= 0)
        {
            return (0, 0);
        }
        var scale = (lane << 16) / _holdLane;
        var k = (int)Math.Clamp((c.Steps3 * scale + (1 << 15)) >> 16, 1, c.Steps3);
        var seen = BitOperations.PopCount(k >= 64 ? c.Mask3 : c.Mask3 & ((1UL << k) - 1));
        var approach = c.AimSq3 <= lane * lane && (c.AimAt3 == 0 || (c.AimAt3 <= c.Steps3 && (c.Mask3 >> (c.AimAt3 - 1) & 1) != 0)) ? 100 : 0;
        return (seen * 100 / k, approach);
    }

    // ---- Candidates ----

    // AmbushCandidates fills Hold's scratch with s's candidates on one side of its zone: the free cells beyond the zone's width on
    // that side within the rules' radius of it (at most the rules' cap looked at, the nearest the zone first: by distance, then
    // row, then column) that its members can reach from where its leader stands, then the garrisonable buildings on that side
    // within the radius whose windows see the zone. The threat is the zone: cover from its nearest point, sight lanes to its
    // middle, start and far end, range and nearness to it. Returns how many cells it looked at.
    private int AmbushCandidates(Router r, Squad s, int side, SquadHoldRules hold, SquadAmbushRules amb)
    {
        _holdCands.Clear();
        ZoneSet(s, side);
        var radius = amb.Radius.Raw;
        var mid = ZonePoint(_ambLen / 2);
        var sp = Cell.Of(mid + Scale(_ambN, amb.Spot));
        var sc = new Cell(Math.Clamp(sp.X, 0, Grid.Width - 1), Math.Clamp(sp.Y, 0, Grid.Height - 1)).Center();
        var f = new Vec(-_ambN.X, -_ambN.Y);
        (_holdSc, _holdF) = (sc, f);
        var aims = (mid, _ambA, _ambB);
        var approach = _ambA - Scale(_ambZ, amb.Approach);
        _ambApproach = approach;
        long laneMax = Fixed.OneRaw;
        foreach (var id in s.MemberList)
        {
            HoldMember(Unit(id)!, out _, out _, out var lane);
            laneMax = Math.Max(laneMax, lane);
        }
        _holdLane = laneMax;
        HoldSightRules();
        var k = SquadClass(r, s);
        var comp = 0;
        if (k >= 0 && HoldFrom(s) is { } from)
        {
            var at = r.NearestPassable(k, Cell.Of(from.Pos), StartSearch);
            comp = at < 0 ? 0 : r.Component(k, at);
        }
        var rc = (int)(radius / Fixed.OneRaw) + 1;
        int x0 = Math.Max(0, (int)(Math.Min(_ambA.X.Raw, _ambB.X.Raw) >> Fixed.Shift) - rc);
        int x1 = Math.Min(Grid.Width - 1, (int)(Math.Max(_ambA.X.Raw, _ambB.X.Raw) >> Fixed.Shift) + rc);
        int y0 = Math.Max(0, (int)(Math.Min(_ambA.Y.Raw, _ambB.Y.Raw) >> Fixed.Shift) - rc);
        int y1 = Math.Min(Grid.Height - 1, (int)(Math.Max(_ambA.Y.Raw, _ambB.Y.Raw) >> Fixed.Shift) + rc);
        _ambCells.Clear();
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var p = new Cell(x, y).Center();
                if (ZoneLat(p) <= amb.Width.Raw)
                {
                    continue; // the zone itself and the other side
                }
                var d = ZoneDist(p);
                if (d <= radius)
                {
                    _ambCells.Add((d, y * Grid.Width + x));
                }
            }
        }
        _ambCells.Sort(); // by distance, then the cell index (row, then column)
        var looked = 0;
        foreach (var (d, key) in _ambCells)
        {
            if (looked >= amb.Cells)
            {
                break;
            }
            looked++;
            var c = new Cell(key % Grid.Width, key / Grid.Width);
            if (Grid.At(c) != Terrain.Clear || (k >= 0 && (!r.Passable(k, r.Index(c)) || (comp != 0 && r.Component(k, r.Index(c)) != comp))))
            {
                continue;
            }
            var p = c.Center();
            var cand = new HoldCand { C = c, Key = key, P = p };
            HoldFill(ref cand, c, hold, aims, laneMax, sc, radius, true, ZoneNearest(p));
            AmbushFill(ref cand, p, d, radius);
            _holdCands.Add(cand);
        }
        foreach (var b in _units)
        {
            if (b.Def.Archetype.Garrison is not { } g || !b.Alive || !b.IsBuilt || (b.Owner != s.Owner && (b.Owner != 0 || g.Owned)))
            {
                continue;
            }
            var o = FootprintOrigin(b);
            int w = FootW(b), h = FootH(b);
            var q = Cell.Of(ZoneNearest(b.Pos));
            var edge = new Cell(Math.Clamp(q.X, o.X, o.X + w - 1), Math.Clamp(q.Y, o.Y, o.Y + h - 1));
            var p = edge.Center();
            var d = ZoneDist(p);
            if (d > radius || ZoneLat(p) <= 0)
            {
                continue; // too far, or on the zone or the other side
            }
            var t = ZoneNearest(p);
            var g0 = Ground;
            var eye = (g0?.HeightAt(p).Raw ?? 0) + WindowRaw;
            var aim = (g0?.HeightAt(t).Raw ?? 0) + BodyRaw;
            if (!LineOfSight(p, Fixed.FromRaw(eye), t, Fixed.FromRaw(aim)))
            {
                continue; // no sight of the zone from its windows on that side
            }
            var room = CapacityOf(b, g) - CountOccupants(b);
            foreach (var p2 in s.PickList)
            {
                if (p2.Building == b.Id && Unit(p2.Unit) is { } m && m.Garrison != b.Id)
                {
                    room--; // promised to a member on his way in
                }
            }
            if (room <= 0)
            {
                continue;
            }
            var (sx, sy) = Octant(t - p);
            var outside = new Cell(edge.X + sx, edge.Y + sy);
            var lanesFrom = Grid.Contains(outside) && Grid.At(outside) == Terrain.Clear ? outside : edge;
            var cand = new HoldCand
            {
                C = lanesFrom, Key = int.MaxValue / 2 + (int)Math.Min(b.Id, int.MaxValue / 2), Building = b.Id, P = p, Room = room,
                Range = (int)g.RangeBonus.Raw,
            };
            HoldFill(ref cand, lanesFrom, hold, aims, laneMax, sc, radius, false, t);
            AmbushFill(ref cand, p, d, radius);
            cand.P = p;
            cand.Cover = 100;
            cand.Trees = 0;
            cand.Safety = Safety(p, sc, f, radius);
            _holdCands.Add(cand);
        }
        return looked;
    }

    // AmbushFill works out a candidate's ambush factors: the distance to the zone (its range and nearness), along and across it, and
    // how little of the zone (its middle, start and far end: Hold's three lanes) it sees.
    private void AmbushFill(ref HoldCand cand, Vec p, long d, long radius)
    {
        cand.DistT = d;
        cand.Near = (int)Math.Clamp(100 - d * 100 / radius, 0, 100);
        cand.Ax = ZoneAx(p);
        cand.Lat = ZoneLat(p);
        cand.Steps3 = -1; // the lane to the approach: worked out when a role weighs it (ApproachLaneOf)
        static bool Seen(ulong mask, int steps, int at) => at == 0 || (at <= steps && (mask >> (at - 1) & 1) != 0);
        var seen = (Seen(cand.Mask0, cand.Steps0, cand.AimAt0) ? 1 : 0) + (Seen(cand.Mask1, cand.Steps1, cand.AimAt1) ? 1 : 0) +
            (Seen(cand.Mask2, cand.Steps2, cand.AimAt2) ? 1 : 0);
        cand.Hidden = 100 - seen * 100 / 3;
    }

    // ApproachLaneOf walks a candidate's lane to the approach point (from its lanes' cell), as far as the scratch's lanes go.
    private void ApproachLaneOf(ref HoldCand cand)
    {
        var (m3, s3, _, a3, q3) = HoldLane(cand.C, _ambApproach, _holdLane, -1);
        (cand.Mask3, cand.Steps3, cand.AimAt3, cand.AimSq3) = (m3, s3, a3, q3);
    }

    // ---- Mines ----

    // PlanAmbushMines plans the line's mines once the side is known: anti-tank mines on the road along the zone (any cell of its width
    // that is road; none: its line), the rules' gap apart from the far end back; anti-personnel mines, then booby traps, on the best
    // cover from the squad's side 1 to 4 cells off the zone's line on the far side (then on the near side if the far one has too
    // few), the far end first among equals, the rules' gap apart. Each kind is dealt round the layers in ID order, one at a time,
    // while they have charges (booby traps: the rules' number each).
    private void PlanAmbushMines(Squad s, SquadHoldRules hold, SquadAmbushRules amb)
    {
        ZoneSet(s, s.AmbushSide);
        var layers = new List<Unit>();
        foreach (var id in s.MemberList)
        {
            var u = Unit(id)!;
            if (amb.Lays(SquadRoleOf(u)))
            {
                layers.Add(u);
            }
        }
        if (layers.Count == 0)
        {
            return;
        }
        var kits = new (int Ability, int Left)[3, layers.Count];
        var need = new int[3];
        int[] archetypes = [amb.AtMine, amb.ApMine, amb.Trap];
        for (var j = 0; j < layers.Count; j++)
        {
            for (var kind = 0; kind < 3; kind++)
            {
                var (ab, left) = Kit(layers[j], archetypes[kind]);
                if (kind == 2)
                {
                    left = Math.Min(left, amb.TrapsEach);
                }
                kits[kind, j] = (ab, left);
                need[kind] += left;
            }
        }
        var at = new List<Cell>();
        var widthCells = (int)(amb.Width.Raw / Fixed.OneRaw);
        for (var d = _ambLen; d >= 0 && at.Count < need[0]; d -= amb.AtGap.Raw)
        {
            var sp = ZonePoint(d);
            Cell? best = null;
            for (var i = 0; i <= 2 * widthCells; i++)
            {
                var off = i == 0 ? 0 : i % 2 == 1 ? (i + 1) / 2 : -(i / 2); // the line, then a cell each way, ...
                var c = Cell.Of(sp + Scale(_ambN, Fixed.FromInt(off)));
                if (!MineCell(s, c) || at.Contains(c))
                {
                    continue;
                }
                if (Ground?.Road(c) == true)
                {
                    best = c;
                    break;
                }
                best ??= c;
            }
            if (best is { } b)
            {
                at.Add(b);
            }
        }
        var ap = new List<Cell>();
        foreach (var sign in (ReadOnlySpan<int>)[-1, 1]) // the far side, then the near side
        {
            if (ap.Count >= need[1] + need[2])
            {
                break;
            }
            ApCells(s, hold, amb, sign, at, ap, need[1] + need[2]);
        }
        var tasks = new List<AmbushMineTask>[layers.Count];
        for (var j = 0; j < layers.Count; j++)
        {
            tasks[j] = [];
        }
        var next = 0;
        Deal(0, at, ref next);
        next = 0;
        Deal(1, ap, ref next);
        Deal(2, ap, ref next);
        for (var j = 0; j < layers.Count; j++)
        {
            s.TaskList.AddRange(tasks[j]);
        }

        void Deal(int kind, List<Cell> spots, ref int i)
        {
            var mk = MineKinds;
            while (i < spots.Count)
            {
                var any = false;
                for (var j = 0; j < layers.Count && i < spots.Count; j++)
                {
                    if (kits[kind, j].Left <= 0)
                    {
                        continue;
                    }
                    kits[kind, j].Left--;
                    tasks[j].Add(new AmbushMineTask(layers[j].Id, spots[i++], mk[kind], kits[kind, j].Ability, AmbushTaskState.Pending));
                    any = true;
                }
                if (!any)
                {
                    break;
                }
            }
        }
    }

    private static readonly AmbushMineKind[] MineKinds = [AmbushMineKind.AntiTank, AmbushMineKind.AntiPersonnel, AmbushMineKind.Trap];

    // ApCells adds to ap (up to want) the cells for anti-personnel mines on one side of the zone (sign -1: the far side), the rules'
    // band off its line, along it, off the road: the best cover from the squad's side first, then the far end, then the cell index; each the
    // rules' gap from the others and a cell from the anti-tank mines.
    private void ApCells(Squad s, SquadHoldRules hold, SquadAmbushRules amb, int sign, List<Cell> at, List<Cell> ap, int want)
    {
        var m = amb.ApFar.Raw + Fixed.OneRaw;
        int x0 = Math.Max(0, (int)((Math.Min(_ambA.X.Raw, _ambB.X.Raw) - m) >> Fixed.Shift));
        int x1 = Math.Min(Grid.Width - 1, (int)((Math.Max(_ambA.X.Raw, _ambB.X.Raw) + m) >> Fixed.Shift));
        int y0 = Math.Max(0, (int)((Math.Min(_ambA.Y.Raw, _ambB.Y.Raw) - m) >> Fixed.Shift));
        int y1 = Math.Min(Grid.Height - 1, (int)((Math.Max(_ambA.Y.Raw, _ambB.Y.Raw) + m) >> Fixed.Shift));
        var list = new List<(int Score, long Ax, int Key)>();
        var five = Fixed.FromInt(5);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var c = new Cell(x, y);
                var p = c.Center();
                var ax = ZoneAx(p);
                var lat = ZoneLat(p) * sign;
                if (ax < 0 || ax > _ambLen || lat < amb.ApNear.Raw || lat > amb.ApFar.Raw || Ground?.Road(c) == true || !MineCell(s, c) ||
                    at.Contains(c) || ap.Contains(c))
                {
                    continue; // (off the road: where infantry run to)
                }
                var score = CoverPoints(c, p + Scale(_ambN, five), hold, Ground?.Forest(c) ?? 0); // cover from the squad's side
                list.Add((score, ax, y * Grid.Width + x));
            }
        }
        list.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Ax != b.Ax ? b.Ax.CompareTo(a.Ax) : a.Key.CompareTo(b.Key));
        var gap = amb.ApGap.Raw * amb.ApGap.Raw;
        var one = (long)Fixed.OneRaw * Fixed.OneRaw;
        foreach (var (_, _, key) in list)
        {
            if (ap.Count >= want)
            {
                return;
            }
            var c = new Cell(key % Grid.Width, key / Grid.Width);
            var p = c.Center();
            var ok = true;
            foreach (var o in ap)
            {
                ok &= (o.Center() - p).LenSq() >= gap;
            }
            foreach (var o in at)
            {
                ok &= (o.Center() - p).LenSq() >= one;
            }
            if (ok)
            {
                ap.Add(c);
            }
        }
    }

    // MineCell: a mine may be planned on c: clear ground on the map, not water, no other of the owner's ambushes plans one there.
    private bool MineCell(Squad s, Cell c)
    {
        if (!Grid.Contains(c) || !Grid.Passable(c) || InWater(c))
        {
            return false;
        }
        foreach (var o in _squads)
        {
            if (o == s || o.Owner != s.Owner || o.TaskList.Count == 0)
            {
                continue;
            }
            foreach (var t in o.TaskList)
            {
                if (t.Cell == c)
                {
                    return false;
                }
            }
        }
        return true;
    }

    // Kit is u's placer ability laying archetype (its component index, or -1) and its charges left.
    private static (int Ability, int Left) Kit(Unit u, int archetype)
    {
        foreach (var ab in u.Def.Abilities)
        {
            if (ab.Effect is PlacerEffect p && p.Archetype == archetype)
            {
                return (ab.Component, u.Charges[ab.Stock]);
            }
        }
        return (-1, 0);
    }

    // ---- The zone's geometry ----

    // ZoneSet loads s's zone into the scratch, with the normal of side (1: the right of start to far end, -1: the left).
    private void ZoneSet(Squad s, int side)
    {
        _ambA = s.AmbushStart.Center();
        _ambB = s.AmbushEnd.Center();
        var d = _ambB - _ambA;
        _ambLen = d.Len().Raw;
        _ambZ = _ambLen == 0 ? new Vec(Fixed.Zero, -Fixed.One) : d.WithLen(Fixed.One);
        var right = new Vec(-_ambZ.Y, _ambZ.X);
        _ambN = side < 0 ? new Vec(-right.X, -right.Y) : right;
    }

    // ZoneAx is how far along the zone from its start p lies (raw); ZoneLat how far out on the side's normal (negative: the other side).
    private long ZoneAx(Vec p) => ((p.X.Raw - _ambA.X.Raw) * _ambZ.X.Raw + (p.Y.Raw - _ambA.Y.Raw) * _ambZ.Y.Raw) >> Fixed.Shift;

    private long ZoneLat(Vec p) => ((p.X.Raw - _ambA.X.Raw) * _ambN.X.Raw + (p.Y.Raw - _ambA.Y.Raw) * _ambN.Y.Raw) >> Fixed.Shift;

    // ZoneDist is p's distance from the zone's line segment (raw).
    private long ZoneDist(Vec p)
    {
        var ax = ZoneAx(p);
        return ax < 0 ? (p - _ambA).Len().Raw : ax > _ambLen ? (p - _ambB).Len().Raw : Math.Abs(ZoneLat(p));
    }

    // ZonePoint is the point `along` (raw) from the zone's start toward its far end; ZoneNearest the zone's point nearest p.
    private Vec ZonePoint(long along) => _ambA + Scale(_ambZ, Fixed.FromRaw(along));

    private Vec ZoneNearest(Vec p) => ZonePoint(Math.Clamp(ZoneAx(p), 0, _ambLen));

    // InZone: p lies in the zone: within w (raw) of its line, between its ends (w beyond them at most).
    private bool InZone(Vec p, long w)
    {
        var ax = ZoneAx(p);
        return ax >= -w && ax <= _ambLen + w && Math.Abs(ZoneLat(p)) <= w;
    }
}
