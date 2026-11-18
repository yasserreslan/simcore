using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The gold economy (docs/design.md, "Gold economy"; the game repo's docs/plans/economy-spec.md, owner 2026-11-08). Gold is the
// player's reserve (Player.Credits): banked by trucks and miners on foot at their side's banks, spent by production and
// construction when they start (only with MatchSetup.Economy; ConstructionSystem.cs, Afford/Charge/Refund). Each tick, after the
// cargo system, every unit on a Mine, Haul or Keep stocked order runs its round, in ID order:
// - Mine: a free work spot (the nearest; all taken: it waits beside the mine), mining there at its rate until it carries a load
//   (or the mine is dry), then on foot to the nearest bank's walk-in spot; round again. Dry and empty-handed, it idles where it is.
//   (Trucks no longer take miners' loads: they harvest by themselves, decision 1088. The order stays for old content and replays.)
// - Haul (the owner, 2026-11-18: the truck "takes the gold from the mine directly"): to the nearest free loading bay of the mine
//   (MineSpec.Bays: one truck loads on each; all held: it queues beside the mine), parked there taking the mine's gold at its own
//   rate (haul.goldPerMinute) until full or the mine is dry, to the nearest bank's unload spot, unloading there, back to the same
//   mine, round again. A bank takes one truck at a time: the truck within 6 cells of a free spot holds it; at a held one it queues
//   there or goes on to the next bank, whichever is sooner by the straight line (the queue's unloads against the drive). Dry: it
//   banks what it carries, then goes on to the nearest other mine with gold, or home to the nearest bank (an event either way).
// - Keep stocked: empty, while the depot has room, to the nearest garage that builds it, buying the depot's room (up to a full
//   load) at haul.supplyGold a supply (short of gold: it waits there), then to the depot, where the cargo system unloads it.
// Nearest is the straight line, ties to the lower ID. Mines and banks are found by ID, their slots checked on use.
public sealed partial class World
{
    private const int MineGoldTicks = TicksPerSecond * 60; // mining work a gold takes: gold a minute added every tick
    private static readonly Fixed MineSpotReach = Fixed.One; // a miner within a cell of its work spot mines
    private static readonly Fixed HaulParkReach = Fixed.Frac(3, 2); // a hauling truck within 1.5 cells of the loading spot is parked
    private static readonly Fixed HaulQueueReach = Fixed.FromInt(6); // a queued truck waits within 6 cells of the loading spot
    private static readonly Fixed BankSpotReach = Fixed.Frac(3, 2); // within 1.5 cells of a bank's spot at its front edge
    private static readonly Fixed HaulClear = Fixed.FromInt(3); // a truck leaving a bay or a bank's spot holds it until 3 cells off

    private readonly List<EconomyEvent> _economyEvents = [];

    /// <summary>The gold economy's events of the last tick, in order.</summary>
    public IReadOnlyList<EconomyEvent> EconomyEvents => _economyEvents;

    /// <summary>
    /// Maps a point of structure s's layout (tenths of a cell from its unturned top-left corner, as content gives a mine's or a
    /// bank's spots) onto the map, turned with its quarter (<see cref="Unit.Quarter"/>).
    /// </summary>
    public static Vec LayoutPoint(Unit s, (int X, int Y) p)
    {
        var a = s.Def.Archetype;
        int w = a.FootprintW * 10, h = a.FootprintH * 10;
        var (x, y) = (s.Quarter & 3) switch
        {
            0 => (p.X, p.Y),
            1 => (h - p.Y, p.X),
            2 => (w - p.X, h - p.Y),
            _ => (p.Y, w - p.X),
        };
        var o = FootprintOrigin(s);
        return new Vec(Fixed.FromRaw((long)o.X * Fixed.OneRaw + (long)x * Fixed.OneRaw / 10),
            Fixed.FromRaw((long)o.Y * Fixed.OneRaw + (long)y * Fixed.OneRaw / 10));
    }

    /// <summary>Mine m's work spot i on the map.</summary>
    public static Vec WorkPoint(Unit m, int i) => LayoutPoint(m, m.Def.Archetype.Mine!.Work[i]);

    /// <summary>Mine m's loading spot on the map (its first loading bay, the art's).</summary>
    public static Vec LoadPoint(Unit m) => LayoutPoint(m, m.Def.Archetype.Mine!.Load);

    /// <summary>Mine m's loading bay i on the map (a hauling truck parks there to load; MineSpec.Bays).</summary>
    public static Vec LoadBayPoint(Unit m, int i) => LayoutPoint(m, m.Def.Archetype.Mine!.Bays[i]);

    /// <summary>
    /// Where a truck unloads at bank b: at its front edge, 1.5 cells out, in line with the art's unload spot (the bank blocks its
    /// footprint, so the yard's lane is not driven yet; decision 1037).
    /// </summary>
    public static Vec UnloadPoint(Unit b) => LayoutPoint(b, (b.Def.Archetype.Bank!.Unload.X, b.Def.Archetype.FootprintH * 10 + 15));

    /// <summary>Where a miner on foot hands in gold at bank b: at its front edge, a cell out, in line with the art's walk-in spot.</summary>
    public static Vec WalkInPoint(Unit b) => LayoutPoint(b, (b.Def.Archetype.Bank!.WalkIn.X, b.Def.Archetype.FootprintH * 10 + 10));

    /// <summary>Mine m's gold and look for the client, or null for anything that is no mine.</summary>
    public MineView? MineStatus(Unit m)
    {
        if (m.Def.Archetype.Mine is not { } ms || m.Econ is not { } me)
        {
            return null;
        }
        var held = 0;
        for (var i = 0; i < me.Slots.Length; i++)
        {
            held += SlotHolder(m, i) is not null ? 1 : 0;
        }
        var loading = 0;
        for (var i = 0; i < me.Bays.Length; i++)
        {
            loading += BayHolder(m, i) is { Econ.Stage: EconStage.Parked } ? 1 : 0;
        }
        var pct = (int)(((long)me.GoldLeft * 100 + ms.Gold - 1) / ms.Gold);
        var stage = me.GoldLeft == 0 ? 3 : me.GoldLeft * 3L > ms.Gold * 2L ? 0 : me.GoldLeft * 3L > ms.Gold ? 1 : 2;
        return new MineView(me.GoldLeft, ms.Gold, pct, stage, held, me.Slots.Length, loading, me.Bays.Length);
    }

    /// <summary>The nearest finished bank of player at p (straight line, ties to the lower ID), or null: then no gold can be delivered.</summary>
    public Unit? NearestBank(byte player, Vec p)
    {
        Unit? best = null;
        long bd = long.MaxValue;
        foreach (var b in _units)
        {
            if (b.Def.Archetype.Bank is not null && b.Owner == player && b.Alive && b.IsBuilt && (b.Pos - p).LenSq() is var d && d < bd)
            {
                (best, bd) = (b, d);
            }
        }
        return best;
    }

    // SlotHolder is the unit holding mine m's work spot i: alive, out in the open, on a Mine order there with that spot; else null.
    private Unit? SlotHolder(Unit m, int i)
    {
        var id = m.Econ!.Slots[i];
        return id != 0 && Unit(id) is { Alive: true, Inside: 0 } h && h.Order.Kind == OrderKind.Mine && h.Order.Target == m.Id &&
            h.Econ is { } he && he.Slot == i ? h : null;
    }

    // BayHolder is the hauling truck on mine m's bay i: parked loading there (alive, out in the open, on a Haul order there with that
    // bay), or the one that last loaded there still within 3 cells of it, leaving (the next waits for it to clear); else null.
    private Unit? BayHolder(Unit m, int i)
    {
        var id = m.Econ!.Bays[i];
        if (id == 0 || Unit(id) is not { Alive: true, Inside: 0 } t || t.Order.Kind != OrderKind.Haul || t.Order.Target != m.Id || t.Econ is not { } te)
        {
            return null;
        }
        return te.Stage == EconStage.Parked ? te.Slot == i ? t : null : Within(t.Pos, LoadBayPoint(m, i), HaulClear) ? t : null;
    }

    // BankHolder is the hauling truck holding bank b's unload spot (driving in from the queue's reach, or unloading), or the one
    // that last unloaded there still within 3 cells of it, leaving; else null.
    private Unit? BankHolder(Unit b)
    {
        var id = b.Econ?.Loader ?? 0;
        if (id == 0 || Unit(id) is not { Alive: true, Inside: 0 } t || t.Order.Kind != OrderKind.Haul || t.Econ is not { } te)
        {
            return null;
        }
        return te.Stage is EconStage.ToBank or EconStage.Unloading && te.Carry > 0 && te.At == b.Id ? t
            : te.Carry == 0 && Within(t.Pos, UnloadPoint(b), HaulClear) ? t : null;
    }

    // OwnBank: unit id if it is a finished bank of t's side, else null.
    private Unit? OwnBank(Unit t, uint id) =>
        id != 0 && Unit(id) is { Alive: true, IsBuilt: true } b && b.Def.Archetype.Bank is not null && b.Owner == t.Owner ? b : null;

    private void RunEconomy()
    {
        foreach (var u in _units)
        {
            if (!u.Alive || u.Inside != 0)
            {
                continue;
            }
            switch (u.Order.Kind)
            {
                case OrderKind.Mine:
                    Mining(u);
                    break;
                case OrderKind.Haul:
                    Hauling(u);
                    break;
                case OrderKind.KeepStocked:
                    Stocking(u);
                    break;
            }
        }
    }

    // MineOrder gives the command's units an order on mine or depot target: Mine (units that mine), Haul (trucks that haul, at a
    // mine), Keep stocked (trucks that haul, at one of the player's depots).
    private void EconomyCommand(Command c)
    {
        var target = Unit(c.Target);
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (target is null || !Commandable(u, c.Player) || u!.Garrison != 0 || u.Inside != 0 || u.Def.Speed.Raw == 0)
            {
                continue;
            }
            var a = u.Def.Archetype;
            var ok = c.Kind switch
            {
                CommandKind.Mine => a.Mining is not null && target.Def.Archetype.Mine is not null,
                CommandKind.Haul => a.Haul is not null && target.Def.Archetype.Mine is not null,
                _ => a.Haul is not null && u.Def.Cargo is not null && IsCargoDepot(target, u),
            };
            if (!ok)
            {
                continue;
            }
            Assign(u, new Order(c.Kind switch { CommandKind.Mine => OrderKind.Mine, CommandKind.Haul => OrderKind.Haul, _ => OrderKind.KeepStocked },
                default, target.Id, 0));
            var e = u.Econ ??= new EconState();
            (e.Stage, e.Mine, e.Slot, e.At, e.Left, e.Told) = (EconStage.None, 0, -1, 0, 0, false); // a fresh round; it keeps what it carries
            e.Work = 0;
        }
    }

    // Mining runs one miner's tick (see the top of this file).
    private void Mining(Unit u)
    {
        if (u.Def.Archetype.Mining is not { } spec || Unit(u.Order.Target) is not { Econ: { } me } m || m.Def.Archetype.Mine is null)
        {
            u.Idle();
            return;
        }
        var e = u.Econ ??= new EconState();
        if (e.Mine != m.Id)
        {
            (e.Mine, e.Slot, e.Work, e.Told) = (m.Id, -1, 0, false);
        }
        if (e.Carry >= spec.Load || (e.Carry > 0 && me.GoldLeft == 0))
        {
            Deliver(u, e);
            return;
        }
        if (me.GoldLeft == 0)
        {
            e.Stage = EconStage.None;
            e.Slot = -1;
            u.Idle(); // dry: it idles where it is (by the mine, or where it handed in its last gold)
            return;
        }
        if (e.Slot < 0 || e.Slot >= me.Slots.Length || me.Slots[e.Slot] != u.Id)
        {
            e.Slot = -1;
            long bd = long.MaxValue;
            for (var i = 0; i < me.Slots.Length; i++)
            {
                if (SlotHolder(m, i) is null && (WorkPoint(m, i) - u.Pos).LenSq() is var d && d < bd)
                {
                    (e.Slot, bd) = (i, d);
                }
            }
            if (e.Slot < 0)
            {
                e.Stage = EconStage.Waiting; // every spot taken: it waits beside the mine for one to free
                Approach(u, m.Pos, WaitReach(m));
                return;
            }
            me.Slots[e.Slot] = u.Id;
        }
        var spot = WorkPoint(m, e.Slot);
        if (!Within(u.Pos, spot, MineSpotReach))
        {
            e.Stage = EconStage.ToSpot;
            Approach(u, spot, MineSpotReach);
            return;
        }
        u.ClearPath();
        e.Stage = EconStage.Working;
        e.Work += spec.GoldPerMinute;
        while (e.Work >= MineGoldTicks && e.Carry < spec.Load && me.GoldLeft > 0)
        {
            e.Work -= MineGoldTicks;
            e.Carry++;
            me.GoldLeft--;
        }
        if (me.GoldLeft == 0)
        {
            MineDry(m);
        }
    }

    // WaitReach: how near mine m a unit waits for a spot: its footprint's half-diagonal (the longer side) and 2 cells.
    private static Fixed WaitReach(Unit m) => Fixed.FromInt(Math.Max(FootW(m), FootH(m)) / 2 + 2);

    // Deliver takes miner u's gold on foot to the nearest bank (none: it waits).
    private void Deliver(Unit u, EconState e)
    {
        if (NearestBank(u.Owner, u.Pos) is not { } bank)
        {
            Wait(u, e);
            return;
        }
        var p = WalkInPoint(bank);
        if (!Within(u.Pos, p, BankSpotReach))
        {
            e.Stage = EconStage.Delivering;
            Approach(u, p, BankSpotReach);
            return;
        }
        u.ClearPath();
        Bank(u, bank, e);
    }

    // Wait stops u for want of a bank, with one event a wait.
    private void Wait(Unit u, EconState e)
    {
        u.ClearPath();
        e.Stage = EconStage.Waiting;
        if (!e.Told)
        {
            e.Told = true;
            _economyEvents.Add(new EconomyEvent(EconomyEventKind.NoBank, u.Owner, u.Id, 0, e.Carry));
        }
    }

    // Bank puts what u carries into its owner's reserve at bank b.
    private void Bank(Unit u, Unit b, EconState e)
    {
        if (Player(u.Owner) is { } p)
        {
            p.Credits += e.Carry;
        }
        _economyEvents.Add(new EconomyEvent(EconomyEventKind.GoldBanked, u.Owner, u.Id, b.Id, e.Carry));
        (e.Carry, e.Stage, e.At, e.Left, e.Told) = (0, EconStage.None, 0, 0, false);
    }

    // MineDry tells each player with units on mine m (on a Mine or Haul order there) that it ran dry, in player order.
    private void MineDry(Unit m)
    {
        foreach (var p in _players)
        {
            foreach (var u in _units)
            {
                if (u.Owner == p.Id && u.Order.Kind is OrderKind.Mine or OrderKind.Haul && u.Order.Target == m.Id)
                {
                    _economyEvents.Add(new EconomyEvent(EconomyEventKind.MineEmpty, p.Id, m.Id, 0, 0));
                    break;
                }
            }
        }
    }

    // Hauling runs one hauling truck's tick (see the top of this file).
    private void Hauling(Unit t)
    {
        if (t.Def.Archetype.Haul is not { } spec || Unit(t.Order.Target) is not { Econ: { } me } m || m.Def.Archetype.Mine is null)
        {
            t.Idle();
            return;
        }
        var e = t.Econ ??= new EconState();
        if (e.Mine != m.Id)
        {
            (e.Mine, e.Stage, e.Slot, e.Work, e.Told) = (m.Id, EconStage.None, -1, 0, false);
        }
        if (e.Stage is EconStage.ToBank or EconStage.Unloading or EconStage.BankQueued)
        {
            ToBank(t, e, spec);
            return;
        }
        if (e.Stage == EconStage.Parked && e.Slot >= 0 && e.Slot < me.Bays.Length && me.Bays[e.Slot] == t.Id)
        {
            Loading(t, e, spec, m, me);
            return;
        }
        if (e.Carry >= spec.Capacity || (me.GoldLeft == 0 && e.Carry > 0))
        {
            ToBank(t, e, spec);
            return;
        }
        if (me.GoldLeft == 0)
        {
            MoveOn(t, e, m);
            return;
        }
        // The nearest free bay (ties: the lower); every bay held: it queues beside the mine.
        e.Slot = -1;
        long bd = long.MaxValue;
        for (var i = 0; i < me.Bays.Length; i++)
        {
            if ((BayHolder(m, i) is null || BayHolder(m, i) == t) && (LoadBayPoint(m, i) - t.Pos).LenSq() is var d && d < bd)
            {
                (e.Slot, bd) = (i, d);
            }
        }
        if (e.Slot < 0)
        {
            e.Stage = EconStage.Queued;
            Approach(t, LoadPoint(m), HaulQueueReach);
            return;
        }
        var bp = LoadBayPoint(m, e.Slot);
        if (!Within(t.Pos, bp, HaulParkReach))
        {
            e.Stage = EconStage.ToMine;
            Approach(t, bp, HaulParkReach);
            return;
        }
        t.ClearPath();
        (e.Stage, e.Work) = (EconStage.Parked, 0);
        me.Bays[e.Slot] = t.Id;
    }

    // Loading runs truck t parked on its bay at mine m: it takes the mine's gold at its rate until full or the mine is dry, then
    // leaves its bay for a bank (none: it stays on it, full), or moves on with nothing.
    private void Loading(Unit t, EconState e, HaulSpec spec, Unit m, EconState me)
    {
        t.ClearPath();
        if (e.Carry < spec.Capacity && me.GoldLeft > 0)
        {
            e.Work += spec.GoldPerMinute;
            while (e.Work >= MineGoldTicks && e.Carry < spec.Capacity && me.GoldLeft > 0)
            {
                e.Work -= MineGoldTicks;
                e.Carry++;
                me.GoldLeft--;
            }
            if (me.GoldLeft == 0)
            {
                MineDry(m); // it took the last gold
            }
            else if (e.Carry < spec.Capacity)
            {
                return; // loading
            }
        }
        if (e.Carry > 0 && NearestBank(t.Owner, t.Pos) is null)
        {
            Wait(t, e); // nowhere to take it: it stays on its bay, full
            e.Stage = EconStage.Parked;
            return;
        }
        (e.Slot, e.Work, e.Told) = (-1, 0, false); // the bay stays its until it is 3 cells off (BayHolder)
        if (e.Carry > 0)
        {
            ToBank(t, e, spec);
            return;
        }
        MoveOn(t, e, m);
    }

    // ToBank drives truck t's gold to a bank's unload spot and unloads it there (spec.UnloadTicks), then sends it back: the nearest
    // bank as it goes; within 6 cells of a free spot it holds it; at one another truck holds, it queues there or goes on to the
    // next bank, whichever is sooner (BankWait).
    private void ToBank(Unit t, EconState e, HaulSpec spec)
    {
        if (e.Stage == EconStage.Unloading && OwnBank(t, e.At) is { } at && Within(t.Pos, UnloadPoint(at), BankSpotReach))
        {
            t.ClearPath();
            if (--e.Left <= 0)
            {
                Bank(t, at, e); // back to the mine next tick (Stage None)
            }
            return;
        }
        if ((OwnBank(t, e.At) ?? NearestBank(t.Owner, t.Pos)) is not { } bank)
        {
            e.At = 0;
            Wait(t, e);
            e.Stage = EconStage.ToBank;
            return;
        }
        e.Told = false;
        var up = UnloadPoint(bank);
        var near = Within(t.Pos, up, HaulQueueReach);
        if (BankHolder(bank) is { } holder && holder != t)
        {
            if (!near)
            {
                e.Stage = EconStage.ToBank; // taken: it decides once there
                Approach(t, up, BankSpotReach);
                return;
            }
            var wait = BankWait(bank, t, spec);
            Unit? next = null;
            foreach (var b in _units)
            {
                if (b != bank && OwnBank(t, b.Id) is not null &&
                    (UnloadPoint(b) - t.Pos).Len().Raw / Math.Max(1, t.Def.Speed.Raw) + BankWait(b, t, spec) is var eta && eta < wait)
                {
                    (next, wait) = (b, eta);
                }
            }
            if (next is not null)
            {
                (e.Stage, e.At) = (EconStage.ToBank, next.Id); // the next bank is sooner
                Approach(t, UnloadPoint(next), BankSpotReach);
                return;
            }
            (e.Stage, e.At) = (EconStage.BankQueued, bank.Id); // it waits its turn here
            Approach(t, up, HaulQueueReach);
            return;
        }
        if (near)
        {
            (bank.Econ ??= new EconState()).Loader = t.Id; // free: it holds the spot
            e.At = bank.Id;
        }
        if (!Within(t.Pos, up, BankSpotReach))
        {
            e.Stage = EconStage.ToBank;
            Approach(t, up, BankSpotReach);
            return;
        }
        t.ClearPath();
        (e.Stage, e.At, e.Left) = (EconStage.Unloading, bank.Id, spec.UnloadTicks);
    }

    // BankWait: the ticks before truck t could start unloading at bank b once there: what the truck holding its spot has left
    // (a full unload while it drives in) and a full unload for each other truck queued there.
    private long BankWait(Unit b, Unit t, HaulSpec spec)
    {
        long wait = BankHolder(b) is { } h && h != t ? h.Econ!.Stage switch
        {
            EconStage.Unloading => h.Econ.Left,
            EconStage.ToBank => spec.UnloadTicks,
            _ => 0, // leaving
        } : 0;
        foreach (var u in _units)
        {
            if (u != t && u.Alive && u.Order.Kind == OrderKind.Haul && u.Econ is { Stage: EconStage.BankQueued } ue && ue.At == b.Id)
            {
                wait += spec.UnloadTicks;
            }
        }
        return wait;
    }

    // MoveOn sends empty truck t on from dry mine m: to the nearest other mine with gold, or home to the nearest bank (none: it
    // idles), with an event either way.
    private void MoveOn(Unit t, EconState e, Unit m)
    {
        Unit? next = null;
        long bd = long.MaxValue;
        foreach (var o in _units)
        {
            if (o != m && o.Def.Archetype.Mine is not null && o.Econ is { GoldLeft: > 0 } && (o.Pos - t.Pos).LenSq() is var d && d < bd)
            {
                (next, bd) = (o, d);
            }
        }
        _economyEvents.Add(new EconomyEvent(EconomyEventKind.HaulMoved, t.Owner, t.Id, next?.Id ?? 0, 0));
        (e.Stage, e.Slot, e.Told) = (EconStage.None, -1, false);
        if (next is not null)
        {
            Assign(t, new Order(OrderKind.Haul, default, next.Id, 0));
            e.Mine = next.Id;
            return;
        }
        if (NearestBank(t.Owner, t.Pos) is { } home)
        {
            Assign(t, new Order(OrderKind.Move, Cell.Of(UnloadPoint(home)), 0, 0));
            return;
        }
        t.Idle();
    }

    // Stocking runs one Keep stocked truck's tick (see the top of this file).
    private void Stocking(Unit t)
    {
        if (t.Def.Archetype.Haul is not { } spec || t.Def.Cargo is not { } cargo || Unit(t.Order.Target) is not { } d || !IsCargoDepot(d, t))
        {
            t.Idle();
            return;
        }
        var e = t.Econ ??= new EconState();
        var depotReach = d.Def.Supply!.Radius - Fixed.One;
        depotReach = depotReach.Raw > 0 ? depotReach : d.Def.Supply.Radius;
        if (t.Cargo > 0)
        {
            (e.Stage, e.Told) = (EconStage.ToDepot, false);
            Approach(t, d.Pos, depotReach); // in its radius the cargo system unloads it (SupplySystem.cs, RunCargo)
            return;
        }
        var room = Math.Min(cargo.Amount, StoreRoom(d));
        if (room <= 0)
        {
            e.Stage = EconStage.Waiting; // full: it waits by the depot
            Approach(t, d.Pos, depotReach);
            return;
        }
        var g = e.Stage == EconStage.Loading && Unit(e.At) is { } ga && IsGarage(ga, t) ? ga : NearestGarage(t);
        if (g is null)
        {
            t.ClearPath();
            e.Stage = EconStage.Waiting;
            return;
        }
        var gp = ExitPoint(g, 0);
        var gr = Fixed.FromInt(2);
        if (!Within(t.Pos, gp, gr))
        {
            e.Stage = EconStage.ToGarage;
            Approach(t, gp, gr);
            return;
        }
        t.ClearPath();
        var price = room * spec.SupplyGold;
        var pl = Player(t.Owner)!;
        var pays = Match.Setup.Economy;
        if (pays && pl.Credits < price)
        {
            e.Stage = EconStage.Waiting;
            if (!e.Told)
            {
                e.Told = true;
                _economyEvents.Add(new EconomyEvent(EconomyEventKind.ShortOfGold, t.Owner, t.Id, g.Id, price - pl.Credits));
            }
            return;
        }
        if (e.Stage != EconStage.Loading || e.At != g.Id)
        {
            (e.Stage, e.At, e.Left, e.Told) = (EconStage.Loading, g.Id, cargo.UnloadTicks, false);
        }
        if (--e.Left > 0)
        {
            return;
        }
        if (pays)
        {
            pl.Credits -= price;
        }
        t.Cargo += room;
        (e.Stage, e.At, e.Left) = (EconStage.ToDepot, 0, 0);
        _economyEvents.Add(new EconomyEvent(EconomyEventKind.SupplyBought, t.Owner, t.Id, g.Id, room));
    }

    // IsGarage: g is t's side's finished factory that builds t (a vehicle garage), with doors (not an airfield's).
    private static bool IsGarage(Unit g, Unit t) =>
        g.Alive && g.IsBuilt && g.Owner == t.Owner && g.Def.Archetype.Production is { IntoStation: false } pr && pr.Builds(t.Def.Archetype.Tags) &&
        g.Airfield == 0;

    private Unit? NearestGarage(Unit t)
    {
        Unit? best = null;
        long bd = long.MaxValue;
        foreach (var g in _units)
        {
            if (IsGarage(g, t) && (ExitPoint(g, 0) - t.Pos).LenSq() is var d && d < bd)
            {
                (best, bd) = (g, d);
            }
        }
        return best;
    }

    // Afford reports whether player may start def: always without the economy; with it, while the reserve holds its cost (else a
    // NotEnoughGold event: factory f, or 0 for a building site).
    private bool Afford(byte player, UnitDef def, uint f)
    {
        if (!Match.Setup.Economy || Player(player) is not { } p || p.Credits >= def.Archetype.Cost)
        {
            return true;
        }
        _economyEvents.Add(new EconomyEvent(EconomyEventKind.NotEnoughGold, player, f, (uint)def.Index, def.Archetype.Cost - p.Credits));
        return false;
    }

    // Charge takes def's cost out of player's reserve (the economy only; never below 0: an airfield's runway segments, laid with
    // its facility, are paid out of what is left).
    private void Charge(byte player, UnitDef def)
    {
        if (Match.Setup.Economy && Player(player) is { } p)
        {
            p.Credits = Math.Max(0, p.Credits - def.Archetype.Cost);
        }
    }

    // Refund gives percent of def's cost back (the economy only): a cancelled production order 100, a cancelled site its refund.
    private void Refund(byte player, UnitDef def, int percent)
    {
        if (Match.Setup.Economy && Player(player) is { } p)
        {
            p.Credits += (int)((long)def.Archetype.Cost * percent / 100);
        }
    }
}
