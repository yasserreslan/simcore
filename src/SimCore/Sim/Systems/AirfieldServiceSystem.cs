using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// What an aircraft gets where it is parked (<see cref="World.ServiceOf"/>), for the aircraft card
/// and the airfield panel. Not game state.
/// </summary>
/// <param name="AtStation">Whether it is parked at a station of its airfield (standing or fallen).</param>
/// <param name="Working">Whether that station serves now (it stands, and its facility stands if it needs one).</param>
/// <param name="RefuelPercent">The refuel rate now, percent of the aircraft's 1x (0: none).</param>
/// <param name="RearmPercent">The rearm rate now, percent of the aircraft's 1x (0: none; always 0 unless it came on a Rearm order).</param>
/// <param name="RepairPercent">The repair rate now, percent of the aircraft's 1x (0: none; always 0 unless it came on a Rearm order).</param>
/// <param name="NextRoundPermille">How far the next round is loaded, 0 to 999.</param>
/// <param name="Serviced">Whether it has everything this stop gives: a full tank, and on a Rearm order full ammunition and HP too.</param>
public readonly record struct AircraftService(bool AtStation, bool Working, int RefuelPercent, int RearmPercent, int RepairPercent,
    int NextRoundPermille, bool Serviced);

// The airfield's service and the aircraft's reactions, milestone M3 part B (the game repo's
// docs/airfield-design.md, sections 1, 1.1 and 2.2):
//  - Service at a parked aircraft's station, integer with remainders: fuel at the station's
//    refuel percent of the aircraft's 1x (refuelTicks for a full tank), one round every
//    rearmTicksPerRound x 100 / rearm percent ticks, HP at repairHpPerSecond x repair percent.
//    A loading station (needsFacility) gives nothing while its airfield's facility is down (rubble
//    or gone); an open stand keeps its 1x. The owner's flight rules (E2, 2026-10-19): an automatic
//    refuel stop (bingo) and a recall refuel only; rearm and repair only on a Rearm order (command
//    29). Serviced means a full tank, and on a Rearm order full ammunition and HP too (HP only
//    where the station and the aircraft repair at all); serviced, a sortie aircraft leaves (back to
//    its previous task) unless held (a recall). At a fallen station (rubble or gone) it gets
//    nothing and stays put until ordered off.
//  - Parked aircraft armour and sight: on the ground an aircraft has its aircraft block's
//    groundArmor and sees groundVision from groundEye, spotted on the fog grid like a ground
//    unit; air defence leaves it alone. It is never an attack target (only splash reaches it).
//  - ReturnToBase (command 28): an aircraft in free flight goes home now. Rearm (command 29): it
//    goes home to rearm (and refuel and repair), then resumes its previous task.
//  - Craters: an aircraft on final whose stretch a crater cuts goes around; one rolling on the
//    runway (roll-out, takeoff roll) toward a fresh crater stops short of it when its brake allows,
//    else runs to its edge and loses the type's craterHitDamage percent of max HP; either way it
//    gives the runway back and taxis off by the nearest connector it can reach. Holding with no
//    usable stretch it diverts: another of the owner's airfields that can take it, else its ground
//    control station, else it holds on (re-checking every second) until repaired or dry.
//  - Stations that fall (rubble or gone) lose their reservations; an aircraft bound for one picks
//    another (holding, on final, or taxiing in: rerouted from the nearest taxi node), else it stops
//    short and waits on the ground for one to free.
public sealed partial class World
{
    // The legs of a roll-out or takeoff roll stopping for a crater (AirOps.Leg).
    private const int LegCraterStop = 7, LegCraterHit = 8;

    /// <summary>
    /// The armor class u has now: an aircraft on the ground at an airfield (any ground phase, or
    /// parked) takes its aircraft block's groundArmor; anything else its own.
    /// </summary>
    public int ArmorOf(Unit u) => u.Def.Archetype.Aircraft is { GroundArmor: >= 0 } ac && Grounded(u) ? ac.GroundArmor : u.Def.Armor;

    // InAir reports whether t is up in the air now: an air unit that is not on the ground at an airfield.
    internal bool InAir(Unit t) => t.Def.Layer == Layers.Air && !(t.Def.Archetype.Aircraft is not null && Grounded(t));

    /// <summary>
    /// Whether station unit s of airfield af serves now: it stands (finished, not rubble) and, if it
    /// needs the facility (a loading station), the facility stands too.
    /// </summary>
    public bool StationWorks(Airfield af, Unit? s) =>
        PieceStands(s) && (s!.Def.Archetype.Airfield is not { Kind: StationKind.Heavy } || PieceStands(Unit(af.Hangar))) &&
        (s.Def.Archetype.Airfield is not { NeedsFacility: true } || PieceStands(Unit(af.Facility)));

    // StationRefuels: station unit s of af stands and, its facility down, its type keeps refuelling then (H1, the
    // main air base: the owner's "landing and refuel go on"); it then gives its refuel only.
    private bool StationRefuelsOnly(Airfield af, Unit? s) =>
        !StationWorks(af, s) && af.Type.RefuelWithoutFacility && PieceStands(s) &&
        (s!.Def.Archetype.Airfield is not { Kind: StationKind.Heavy } || PieceStands(Unit(af.Hangar)));

    /// <summary>What aircraft u gets where it is parked now (all zero while it is not parked).</summary>
    public AircraftService ServiceOf(Unit u)
    {
        if (u.AirOps is not { Phase: FlightPhase.Parked } ops || u.Def.Archetype.Aircraft is not { } ac || Airfield(ops.Airfield) is not { } af)
        {
            return default;
        }
        var spec = ServingSpec(af, ops);
        var per = (long)ac.RearmTicksPerRound * 100;
        var full = RearmVisit(u);
        return new AircraftService(ops.Station != 0, spec is not null, spec?.RefuelPercent ?? 0, full ? spec?.RearmPercent ?? 0 : 0,
            full ? spec?.RepairPercent ?? 0 : 0, (int)(ops.AmmoAcc * 1000 / per), Serviced(u, ac, spec, full, StationStore(af, ops) is null));
    }

    // StationStore is the limited stock the service at aircraft ops's station draws on (logistics: its own airfield's core's, the
    // forward airbase), or null: free (a main air base, a forward airfield).
    private Unit? StationStore(Airfield af, AirOps ops) =>
        StationOf(af, ops.Station).St is { } st && Unit(st.Unit) is { } s ? AirfieldStore(AirfieldOf(s) ?? af) : AirfieldStore(af);

    // RoundCost is what one round of stock i of u costs from store (logistics): its weapon's depot reload cost when it has one, else the
    // store's round cost.
    private static int RoundCost(Unit u, int stock, Unit store)
    {
        foreach (var w in u.Def.Weapons)
        {
            if (w.Stock == stock && w.Effect.DepotReload is { } dr)
            {
                return dr.Cost;
            }
        }
        return store.Def.Supply!.RoundCost;
    }

    // Pay takes cost out of store's stock (null: free) when it holds that much; false when it does not.
    private static bool Pay(Unit? store, int cost)
    {
        if (store is null || cost <= 0)
        {
            return true;
        }
        if (store.Stock < cost)
        {
            return false;
        }
        store.Stock -= cost;
        return true;
    }

    // RearmVisit: whether u's stop gives the full service (it came on a Rearm order); an automatic
    // refuel stop or a recall refuels only (the owner's rules).
    private static bool RearmVisit(Unit u) => u.HomeReason == HomeReason.Rearm;

    // ServingSpec is the airfield block of the station aircraft ops is parked at while it works, or null.
    private AirfieldPieceSpec? ServingSpec(Airfield af, AirOps ops) =>
        StationOf(af, ops.Station).St is { } st && Unit(st.Unit) is { } s && (AirfieldOf(s) ?? af) is var own // (H1: a linked field's station by its own rules)
            ? StationWorks(own, s) ? s.Def.Archetype.Airfield : StationRefuelsOnly(own, s) ? s.Def.Archetype.Airfield!.RefuelOnly : null
            : null;

    // ShortStock is u's first stock below its maximum, or -1. Without cargo (a station that does not load pallets: logistics, an
    // airfield with a limited stock) its cargo pallets are left out.
    private static int ShortStock(Unit u, bool cargo = true)
    {
        var skip = cargo && u.Lift == 0 ? -1 : AirdropWeapon(u)?.Stock ?? -1; // (no pallets while it carries vehicles: AirliftSystem.cs)
        for (var i = 0; i < u.Charges.Count; i++)
        {
            if (u.Charges[i] < u.StockMax(i) && i != skip)
            {
                return i;
            }
        }
        return -1;
    }

    // ShortSalvo is u's first countermeasure dispenser below its salvos per sortie, or -1 (E6).
    private static int ShortSalvo(Unit u)
    {
        var cms = u.Def.Archetype.Countermeasures;
        for (var i = 0; i < cms.Count; i++)
        {
            if (u.Salvos[i] < cms[i].Salvos)
            {
                return i;
            }
        }
        return -1;
    }

    // Serviced: a full tank and, for the full service (full), full ammunition and full HP where it
    // can be repaired (the aircraft repairs and the station, when it serves, repairs too).
    private static bool Serviced(Unit u, AircraftSpec ac, AirfieldPieceSpec? spec, bool full, bool cargo = true) =>
        (u.Tank <= 0 || u.Lifetime >= u.Tank) && (!full || (ShortStock(u, cargo) < 0 && ShortSalvo(u) < 0 &&
        (u.HP >= u.Def.MaxHp || ac.RepairHpPerSecond == 0 || spec is { RepairPercent: 0 })));

    // Service gives parked aircraft u a tick of its station's service (spec: the working station's
    // block, or null for none): fuel, and on a Rearm order (full) ammunition and HP too. It reports
    // whether it is serviced.
    // Logistics: at a station whose airfield holds a limited stock (store: the forward airbase's) every part is paid out of it: fuel
    // and HP pro rata (the store's fuel cost a full tank, its repair cost per 100 HP; nothing while the stock is empty), a round
    // (or a countermeasure salvo) when it is loaded (its cost, else it waits); cargo pallets are never loaded there.
    private static bool Service(Unit u, AircraftSpec ac, AirOps ops, AirfieldPieceSpec? spec, bool full, Unit? store = null)
    {
        var max = u.Tank; // E13 (D-FUEL1): its tank on this map
        var sup = store?.Def.Supply;
        if (spec is not null && max > 0 && u.Lifetime < max && (store is null || store.Stock > 0))
        {
            var per = (long)ac.RefuelTicks * 100;
            ops.FuelAcc += (long)max * spec.RefuelPercent;
            var was = u.Lifetime;
            u.Lifetime = (int)Math.Min(max, u.Lifetime + ops.FuelAcc / per);
            ops.FuelAcc %= per;
            if (sup is { FuelCost: > 0 })
            {
                ops.FuelCostAcc += (long)(u.Lifetime - was) * sup.FuelCost;
                var pay = (int)Math.Min(store!.Stock, ops.FuelCostAcc / max);
                store.Stock -= pay;
                ops.FuelCostAcc -= (long)pay * max;
                ops.FuelCostAcc = Math.Min(ops.FuelCostAcc, max - 1L); // (the stock ran out: the rest is not owed)
            }
        }
        if (max <= 0 || u.Lifetime >= max)
        {
            ops.FuelAcc = 0;
        }
        var cargo = store is null;
        if (full && spec is not null && spec.RearmPercent > 0 && ShortStock(u, cargo) is var stock and >= 0)
        {
            var per = (long)ac.RearmTicksPerRound * 100;
            ops.AmmoAcc += spec.RearmPercent;
            if (store is not null)
            {
                ops.AmmoAcc = Math.Min(per, ops.AmmoAcc); // a round waits for the stock to pay for it
            }
            if (ops.AmmoAcc >= per && Pay(store, store is null ? 0 : RoundCost(u, stock, store)))
            {
                ops.AmmoAcc -= per;
                u.MutableCharges[stock]++;
            }
        }
        else if (full && spec is not null && spec.RearmPercent > 0 && ShortSalvo(u) is var cm and >= 0)
        {
            // Ammunition full: the countermeasure dispensers, a salvo at a time at their own rate (E6).
            var per = (long)u.Def.Archetype.Countermeasures[cm].RearmTicks * 100;
            ops.AmmoAcc += spec.RearmPercent;
            if (store is not null)
            {
                ops.AmmoAcc = Math.Min(per, ops.AmmoAcc);
            }
            if (ops.AmmoAcc >= per && Pay(store, sup?.RoundCost ?? 0))
            {
                ops.AmmoAcc -= per;
                u.MutableSalvos[cm]++;
            }
        }
        if (ShortStock(u, cargo) < 0 && ShortSalvo(u) < 0)
        {
            ops.AmmoAcc = 0;
        }
        if (full && spec is not null && spec.RepairPercent > 0 && ac.RepairHpPerSecond > 0 && u.HP < u.Def.MaxHp && (store is null || store.Stock > 0))
        {
            var per = (long)TicksPerSecond * 100;
            ops.HpAcc += (long)ac.RepairHpPerSecond * spec.RepairPercent;
            var was = u.HP;
            u.HP = (int)Math.Min(u.Def.MaxHp, u.HP + ops.HpAcc / per);
            ops.HpAcc %= per;
            if (sup is { RepairCost: > 0 })
            {
                ops.HpCostAcc += (long)(u.HP - was) * sup.RepairCost;
                var pay = (int)Math.Min(store!.Stock, ops.HpCostAcc / 100);
                store.Stock -= pay;
                ops.HpCostAcc = Math.Min(ops.HpCostAcc - pay * 100L, 99);
            }
        }
        if (u.HP >= u.Def.MaxHp)
        {
            ops.HpAcc = 0;
        }
        return Serviced(u, ac, spec, full, cargo);
    }

    // ---- ReturnToBase and Rearm ----

    // ReturnToBaseCommand: each listed aircraft of the player that flies free turns for home now
    // (to the target's airfield when that is the player's and can take it, else its best home);
    // climbing out, it does so once it flies free; holding at another airfield, a target airfield
    // that can take it diverts it there. On the ground, or on final, it is ignored. Recalled to an
    // airfield it stays there once refuelled (held; not rearmed) until its next order; its ground
    // control station relaunches it as always (with the stocks it came home with).
    private void ReturnToBaseCommand(Command c) => HomeCommand(c, HomeReason.Recall);

    // RearmCommand (the owner's rules: the only way to rearm): each listed aircraft goes home as for
    // ReturnToBase, but to be refuelled, rearmed and repaired, and then it resumes its previous task
    // (decision D10). Already landing or parked it takes the full service where it is (a recall's
    // hold ends); leaving (pushing back to climbing out) it comes back once it flies free.
    private void RearmCommand(Command c) => HomeCommand(c, HomeReason.Rearm);

    // HomeCommand runs ReturnToBase (reason Recall) or Rearm (reason Rearm) for c's aircraft.
    private void HomeCommand(Command c, HomeReason reason)
    {
        var target = Unit(c.Target) is { } tu && tu.Owner == c.Player ? AirfieldOf(tu) : null;
        foreach (var id in c.Units)
        {
            if (Unit(id) is not { Alive: true, Inside: 0 } u || u.Owner != c.Player || u.Def.Archetype.Flight is null)
            {
                continue;
            }
            var ac = u.Def.Archetype.Aircraft;
            var rearm = reason == HomeReason.Rearm;
            switch (u.AirOps)
            {
                case null:
                    GoHome(u, target, reason);
                    break;
                case { Phase: FlightPhase.Climb } ops:
                    (ops.Recall, ops.RecallReason) = (true, reason);
                    break;
                case { Phase: >= FlightPhase.PushBack and <= FlightPhase.TakeoffRoll } ops when rearm:
                    (ops.Recall, ops.RecallReason) = (true, reason); // leaving: back once it flies free
                    break;
                case { Phase: FlightPhase.Holding } ops when target is not null && target.Id != ops.Airfield && ac is not null &&
                    RunwayFor(target, ac, out _):
                    if (Airfield(ops.Airfield) is { } from)
                    {
                        LeaveAirfield(u, from, ops);
                    }
                    MoveTo(u, target, ac, ops);
                    if (rearm)
                    {
                        (u.HomeReason, u.AirOps!.Hold) = (reason, false);
                    }
                    break;
                case { } ops when rearm:
                    (u.HomeReason, ops.Hold, ops.Launch) = (reason, false, false); // landing or parked: the full service here, then it resumes
                    break;
            }
        }
    }

    // GoHome sends aircraft u, flying free, home now for `reason` (a recall or a Rearm order): to
    // airfield `to` when it can take it, else its best home (an airfield; else its ground control
    // station). Recalled to an airfield it is held there once refuelled; sent to rearm it leaves
    // again once serviced, back to where it orbited, unless `hold` (a gunship out of rounds: held
    // there, rearmed, until its next order).
    private void GoHome(Unit u, Airfield? to, HomeReason reason, bool hold = false)
    {
        u.Idle();
        u.Homing = true;
        u.HomeReason = reason;
        if (_airfields.Count == 0 || u.Def.Archetype.Aircraft is not { Landing: LandingKind.Runway } ac)
        {
            return;
        }
        var field = to is not null && to.Owner == u.Owner && RunwayFor(to, ac, out _) ? to : HomeAirfield(u, ac, HomeStation(u) is not null);
        if (field is not null)
        {
            JoinAirfield(u, field, ac);
            u.AirOps!.Hold = hold || reason == HomeReason.Recall;
        }
    }

    // ---- Leaving, diverting ----

    // LeaveAirfield gives up u's business at af: its reservation, its place in the queue, the runway.
    private void LeaveAirfield(Unit u, Airfield af, AirOps ops)
    {
        if (StationOf(af, ops.Station).St is { } st && st.ReservedBy == u.Id)
        {
            st.ReservedBy = 0;
        }
        Sim.Airfield.Dequeue(af.LandingList, u.Id);
        Sim.Airfield.Dequeue(af.TakeoffList, u.Id);
        af.ApproachList.Remove(u.Id);
        if (af.RunwayLock == u.Id)
        {
            af.RunwayLock = 0;
        }
        ops.Station = 0;
    }

    // MoveTo makes holding aircraft u hold at airfield `to` instead, keeping where it goes back to
    // and its hold.
    private void MoveTo(Unit u, Airfield to, AircraftSpec ac, AirOps ops)
    {
        var (ret, hold) = (ops.ReturnCell, ops.Hold);
        JoinAirfield(u, to, ac);
        u.AirOps!.ReturnCell = ret;
        u.AirOps.Hold = hold;
    }

    // HomeStation is u's ground control station while it can take it back, or null.
    private Unit? HomeStation(Unit u) =>
        Unit(u.Source) is { Alive: true } s && s.Owner == u.Owner && s.Def.Ability(LaunchOf(s, u)) is not null ? s : null;

    // Divert: holding aircraft u whose airfield has no usable stretch for it goes to another of the
    // owner's airfields with one and a free station (nearest to it, ties by ID), else to its ground
    // control station; with neither it holds on. Returns whether it left.
    private bool Divert(Unit u, AircraftSpec ac, Airfield af, AirOps ops)
    {
        Airfield? best = null;
        long bestD = 0;
        foreach (var o in _airfields)
        {
            if (o == af || o.Owner != u.Owner || !RunwayFor(o, ac, out var rw) || !HasFreeStation(o, ac))
            {
                continue;
            }
            var d = (HoldPoint(o, rw) - u.Pos).LenSq();
            if (best is null || d < bestD)
            {
                (best, bestD) = (o, d);
            }
        }
        if (best is null && HomeStation(u) is null)
        {
            return false;
        }
        LeaveAirfield(u, af, ops);
        _airfieldEvents.Add(new AirfieldEvent(AirfieldEventKind.Diverted, u.Id, af.Id, u.Pos));
        if (best is not null)
        {
            MoveTo(u, best, ac, ops);
            return true;
        }
        u.AirOps = null; // free flight, homing: its ground control station (the scout system)
        u.Homing = true;
        u.OrbitAt = ops.ReturnCell.Center();
        return true;
    }

    // ---- Stations that fall ----

    // DropFallenStation: an aircraft bound for a station (holding, on final, rolling out, taxiing
    // in or parking) whose station no longer stands gives it up; holding, it also leaves the landing
    // queue until it has another.
    private void DropFallenStation(Unit u, Airfield af, AirOps ops)
    {
        if (ops.Station == 0 || ops.Phase > FlightPhase.Parking)
        {
            return;
        }
        var (_, st) = StationOf(af, ops.Station);
        if (st is not null && StationStands(af, st))
        {
            return;
        }
        if (st is not null && st.ReservedBy == u.Id)
        {
            st.ReservedBy = 0;
        }
        ops.Station = 0;
        if (ops.Phase == FlightPhase.Holding)
        {
            Sim.Airfield.Dequeue(af.LandingList, u.Id);
        }
    }

    // PickStation finds the best free station of af for u from taxi node `from`: a working station
    // of the kind it wants (a loading station when it came to rearm and needs rearm or repair, else an open stand),
    // then a working one of the other kind, then one that does not work; then the shortest taxi,
    // then the station ID. Null when none is free and reachable.
    private AirfieldStation? PickStation(Unit u, Airfield af, AircraftSpec ac, TaxiGraph g, int from)
    {
        var (dist, _) = TaxiRoute(g, from);
        var want = RearmVisit(u) && NeedsService(u) ? StationKind.Loading : StationKind.Normal; // a refuel stop leaves the shelters free
        if (ac.SizeClass >= AircraftClass.L)
        {
            want = StationKind.Heavy; // (H1: a heavy takes a heavy bay whatever it came for)
        }
        AirfieldStation? best = null;
        int bestPref = 0;
        long bestD = 0;
        foreach (var r in ServedRows(af)) // (H1: a base's linked forward airfields' stations too, by its joined taxi graph)
        {
            foreach (var st in r.Stations)
            {
                if (st.Occupant != 0 || st.ReservedBy != 0 || !Fits(af, st, ac))
                {
                    continue;
                }
                var n = FindStop(g, st.Unit);
                if (n < 0 || dist[n] == long.MaxValue)
                {
                    continue;
                }
                var s = Unit(st.Unit);
                var own = r.Field ?? af;
                var pref = StationWorks(own, s) ? (st.Kind == want ? 0 : 1) : StationRefuelsOnly(own, s) ? 2 : 3; // (H1: refuel-only next)
                if (best is null || pref < bestPref || (pref == bestPref && (dist[n] < bestD || (dist[n] == bestD && st.Unit < best.Unit))))
                {
                    (best, bestPref, bestD) = (st, pref, dist[n]);
                }
            }
        }
        return best;
    }

    // NearestTaxiNode is the taxiway, lane mouth or lane node of g nearest to p (ties: lower index), or -1.
    private static int NearestTaxiNode(TaxiGraph g, Vec p)
    {
        var best = -1;
        long bestD = 0;
        for (var i = 0; i < g.Nodes.Count; i++)
        {
            if (g.Nodes[i].Kind is not (TaxiNodeKind.TaxiwayConnector or TaxiNodeKind.LaneMouth or TaxiNodeKind.Lane))
            {
                continue;
            }
            var d = (g.Nodes[i].At - p).LenSq();
            if (best < 0 || d < bestD)
            {
                (best, bestD) = (i, d);
            }
        }
        return best;
    }

    // TaxiToStation sends grounded aircraft u from where it stands, through taxi node `from` (-1:
    // the nearest taxi node), the taxi graph's shortest way to the lane of its station and the
    // station's park-in path: its own reservation when it still has one, else the best free
    // station (reserved now). It stops first and turns on the spot. Returns false when no station
    // is free and reachable.
    private bool TaxiToStation(Unit u, AircraftSpec ac, Airfield af, AirOps ops, int from)
    {
        var t = af.Type;
        var g = AirfieldTaxiGraph(af);
        from = from >= 0 ? from : NearestTaxiNode(g, u.Pos);
        if (from < 0)
        {
            return false;
        }
        var (row, st) = StationOf(af, ops.Station);
        if (st is null || st.ReservedBy != u.Id || !StationStands(af, st))
        {
            st = PickStation(u, af, ac, g, from);
            row = st is null ? null : StationOf(af, st.Unit).Row;
        }
        if (st is null || row is null)
        {
            return false;
        }
        var lane = RowLane(g, t, row);
        var (_, prev) = TaxiRoute(g, from);
        var pts = _pathScratch;
        pts.Clear();
        pts.Add(u.Pos);
        var nodes = new List<int> { -1 };
        if (lane < 0 || !AddRoute(g, prev, from, lane, pts, nodes))
        {
            return false;
        }
        var mouth = 1;
        for (var i = 1; i < nodes.Count; i++)
        {
            if (g.Nodes[nodes[i]].Kind == TaxiNodeKind.Lane)
            {
                mouth = Math.Max(1, i - 1); // the node before the first lane node
                break;
            }
        }
        var laneAt = pts.Count - 1;
        var slot = SlotsOf(t, row)[st.Slot];
        for (var i = 1; i < slot.ParkIn.Count; i++)
        {
            pts.Add(SlotPoint(af, row, slot.ParkIn[i]));
        }
        var at = new int[pts.Count];
        SetPath(ops, pts, at);
        (ops.MarkA, ops.MarkMouth, ops.MarkB) = (-1, at[mouth], at[laneAt]);
        st.ReservedBy = u.Id;
        ops.Station = st.Unit;
        ops.Phase = FlightPhase.TaxiIn;
        ops.Speed = 0;
        ops.Wait = 0;
        return true;
    }

    // NoStationAhead: taxiing in, its station fell and no other is free: it stops at its lane mouth
    // if it is not past it yet, else at the next point of its way, and waits there on the ground.
    private static void NoStationAhead(AirOps ops)
    {
        if (ops.MarkMouth < 0)
        {
            return; // no station from the start: on to the end of its way (off the runway)
        }
        var stop = Math.Min(ops.PathPts.Count - 1, ops.PathIdx <= ops.MarkMouth ? ops.MarkMouth : ops.PathIdx);
        ops.PathPts.RemoveRange(stop + 1, ops.PathPts.Count - stop - 1);
        ops.PathTurn.RemoveRange(stop + 1, ops.PathTurn.Count - stop - 1);
        (ops.MarkMouth, ops.MarkB) = (-1, ops.PathPts.Count);
    }

    // SlotAt finds the row and slot of af whose stop mark is nearest to p, within a cell (a parked
    // aircraft whose station is gone still leaves by its park-out path).
    private (AirfieldRow? Row, int Slot) SlotAt(Airfield af, Vec p)
    {
        AirfieldRow? best = null;
        var slot = -1;
        var bestD = (long)Fixed.OneRaw * Fixed.OneRaw;
        foreach (var r in ServedRows(af))
        {
            var slots = SlotsOf(af.Type, r);
            for (var i = 0; i < slots.Count; i++)
            {
                var d = (SlotPoint(af, r, slots[i].ParkIn[^1]) - p).LenSq();
                if (d <= bestD)
                {
                    (best, slot, bestD) = (r, i, d);
                }
            }
        }
        return (best, slot);
    }

    // ---- Craters and the runway ----

    // StretchOpen reports whether no open crater closes any of af's runway between x west and east (raw).
    private static bool StretchOpen(Airfield af, long west, long east)
    {
        foreach (var c in af.CraterList)
        {
            if (c.Repaired)
            {
                continue;
            }
            var r = c.Radius.Raw + af.Type.CraterMargin.Raw;
            if (c.X.Raw - r < east && c.X.Raw + r > west)
            {
                return false;
            }
        }
        return true;
    }

    // GeoOf is the runway geometry of the stretch from x west to east (raw) of af: its westmost and
    // eastmost connectors' segments.
    private static RunwayGeo GeoOf(Airfield af, long west, long east)
    {
        var t = af.Type;
        int k0 = FirstConnector(af), k1 = k0;
        var any = false;
        for (var k = af.West; k <= af.East; k++)
        {
            if (!t.HasConnector(k))
            {
                continue;
            }
            var x = ConnectorX(t, k);
            if (x >= west && x <= east)
            {
                k0 = any ? k0 : k;
                k1 = k;
                any = true;
            }
        }
        return new RunwayGeo(k0, k1, west, east, (long)t.RunwayCells * Fixed.OneRaw / 2);
    }

    // HoldGeo is the stretch aircraft ac would use at af, or with none usable the whole runway (it
    // holds off the runway's east end all the same).
    private RunwayGeo HoldGeo(Airfield af, AircraftSpec ac)
    {
        if (RunwayFor(af, ac, out var rw))
        {
            return rw;
        }
        var t = af.Type;
        long west = (long)t.SegmentCells * af.West * Fixed.OneRaw, east = (long)t.SegmentCells * (af.East + 1) * Fixed.OneRaw;
        var usable = UsableEast(af, ac, west, east); // a displaced threshold where its corridor needs one (E9)
        return GeoOf(af, west, usable > west ? usable : east);
    }

    // RollCheck: aircraft u rolling on the runway (dir -1: the roll-out toward the facility, +1: the
    // takeoff roll) with an open crater ahead of it before it turns off (the roll-out's exit turn)
    // or leaves the ground (the distance to its takeoff speed): it brakes for the crater's near
    // edge, stopping short of it when its brake allows (v^2 / 2 brake within the distance), else it
    // runs to the edge and is hurt there. Either way the runway lock is given back. Returns whether
    // it reacted.
    private bool RollCheck(Unit u, AircraftSpec ac, Airfield af, AirOps ops, int dir)
    {
        var (lx, ly) = MapToLocal(af, u.Pos);
        long far;
        if (dir < 0)
        {
            far = ops.MarkA >= 0 && ops.MarkA < ops.PathPts.Count ? MapToLocal(af, ops.PathPts[ops.MarkA]).X : lx;
        }
        else
        {
            long v0 = ops.Speed, vt = ac.TakeoffSpeed.Raw * TicksPerSecond;
            far = lx + (vt > v0 ? (vt * vt - v0 * v0) / (40 * Pace(TakeoffAccelOf(ac, ops), TempoOf(af.Type, ops))) : 0); // (its roll at the leave tempo)
        }
        long best = -1;
        foreach (var c in af.CraterList)
        {
            if (c.Repaired)
            {
                continue;
            }
            long a = c.X.Raw - c.Radius.Raw, b = c.X.Raw + c.Radius.Raw;
            long d;
            if (a < lx && b > lx)
            {
                d = 0; // it opened under it
            }
            else if (dir < 0 && b <= lx && b > far)
            {
                d = lx - b;
            }
            else if (dir > 0 && a >= lx && a < far)
            {
                d = a - lx;
            }
            else
            {
                continue;
            }
            if (best < 0 || d < best)
            {
                best = d;
            }
        }
        if (best < 0)
        {
            return false;
        }
        var v = ops.Speed;
        ops.Leg = v * v <= 40 * Pace2(ac.Brake.Raw, TempoOf(af.Type, ops)) * best ? LegCraterStop : LegCraterHit; // (the brake at its pace, as Taxi)
        if (af.RunwayLock == u.Id)
        {
            af.RunwayLock = 0;
        }
        var pts = _pathScratch;
        pts.Clear();
        pts.Add(u.Pos);
        pts.Add(LocalToMap(af, lx + dir * best, ly));
        SetPath(ops, pts, new int[pts.Count]);
        (ops.MarkA, ops.MarkB, ops.MarkMouth) = (-1, -1, -1);
        return true;
    }

    // CraterStop brakes u (stopping for a crater, RollCheck) to the end of its way; stopped, it
    // takes the crater damage if it ran into the edge, then taxis off by the nearest connector it
    // can reach without crossing a crater: a landing aircraft on to its station, one that was taking
    // off back to the holding point to wait for the runway.
    private void CraterStop(Unit u, AircraftSpec ac, Airfield af, AirOps ops)
    {
        if (!Taxi(u, ac, ops, TaxiV(af.Type, ac) * TicksPerSecond, true, -1, false))
        {
            return;
        }
        var hit = ops.Leg == LegCraterHit;
        if (hit)
        {
            u.HP -= Math.Max(1, (int)((long)u.Def.MaxHp * af.Type.CraterHitDamage / 100));
            if (u.HP <= 0)
            {
                u.KilledBy = KillCause.Crash; // it ran into the crater and was wrecked there (E9; on the ground: no wreck, no ejection)
            }
        }
        _airfieldEvents.Add(new AirfieldEvent(hit ? AirfieldEventKind.CraterHit : AirfieldEventKind.CraterStop, u.Id, af.Id, u.Pos));
        var landing = ops.Phase == FlightPhase.Rollout;
        ops.Leg = 0;
        ops.Speed = 0;
        var g = AirfieldTaxiGraph(af);
        var k = ReachableConnector(af, u.Pos);
        var from = k < 0 ? -1 : FindNode(g, TaxiNodeKind.RunwayConnector, ConnectorTenths(af.Type, k), af.Type.RunwayCells * 5);
        if (landing)
        {
            if (from >= 0 && TaxiToStation(u, ac, af, ops, from))
            {
                return;
            }
        }
        else if (from >= 0)
        {
            TaxiOutFrom(u, ac, af, ops, [], from);
            return;
        }
        // Nowhere to go from here: it waits on the ground where it stopped.
        if (StationOf(af, ops.Station).St is { } st && st.ReservedBy == u.Id)
        {
            st.ReservedBy = 0;
        }
        ops.Station = 0;
        ops.Phase = FlightPhase.Parked;
    }

    // ReachableConnector is the segment of af's connector nearest to p along the runway that no
    // open crater separates from it (ties: the lower segment), or -1.
    private static int ReachableConnector(Airfield af, Vec p)
    {
        var (lx, _) = AirfieldMapToLocal(af, p);
        var best = -1;
        long bestD = 0;
        for (var k = af.West; k <= af.East; k++)
        {
            if (!af.Type.HasConnector(k))
            {
                continue;
            }
            var x = ConnectorX(af.Type, k);
            long lo = Math.Min(x, lx), hi = Math.Max(x, lx);
            var blocked = false;
            foreach (var c in af.CraterList)
            {
                if (!c.Repaired && c.X.Raw - c.Radius.Raw < hi && c.X.Raw + c.Radius.Raw > lo)
                {
                    blocked = true;
                    break;
                }
            }
            var d = hi - lo;
            if (!blocked && (best < 0 || d < bestD))
            {
                (best, bestD) = (k, d);
            }
        }
        return best;
    }
}
