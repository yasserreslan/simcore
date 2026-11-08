using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The gold economy (docs/design.md, "Gold economy"; the game repo's docs/plans/economy-spec.md, owner 2026-11-08). Gold is the
// player's reserve (Player.Credits): banked by trucks and miners on foot at their side's banks, spent by production and
// construction when they start (only with MatchSetup.Economy; ConstructionSystem.cs, Afford/Charge/Refund). Each tick, after the
// cargo system, every unit on a Mine, Haul or Keep stocked order runs its round, in ID order:
// - Mine: a free work spot (the nearest; all taken: it waits beside the mine), mining there at its rate until it carries a load
//   (or the mine is dry), then the load into its side's hauling truck parked at the mine, handed in from its spot (what fits; the
//   rest it keeps and mines on to a load again); while its side has a truck on Haul there that is away (or full), it waits for it at its spot; with none, on
//   foot to the nearest bank's walk-in spot; round again. Dry and empty-handed, it idles where it is.
// - Haul: to the mine's loading spot (another truck there: it queues beside the mine), parked there taking loads until full, to the
//   nearest bank's unload spot, unloading there, back. Dry: once its own side's miners have handed in what they carry, it banks
//   what it has, then goes on to the nearest other mine its side's miners work, or home to the nearest bank.
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

    /// <summary>Mine m's loading spot on the map (a hauling truck parks there).</summary>
    public static Vec LoadPoint(Unit m) => LayoutPoint(m, m.Def.Archetype.Mine!.Load);

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
        var pct = (int)(((long)me.GoldLeft * 100 + ms.Gold - 1) / ms.Gold);
        var stage = me.GoldLeft == 0 ? 3 : me.GoldLeft * 3L > ms.Gold * 2L ? 0 : me.GoldLeft * 3L > ms.Gold ? 1 : 2;
        return new MineView(me.GoldLeft, ms.Gold, pct, stage, held, me.Slots.Length);
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

    // ParkedTruck is the hauling truck parked on mine m's loading spot, or null.
    private Unit? ParkedTruck(Unit m)
    {
        var id = m.Econ!.Loader;
        return id != 0 && Unit(id) is { Alive: true, Inside: 0 } t && t.Order.Kind == OrderKind.Haul && t.Order.Target == m.Id &&
            t.Econ is { Stage: EconStage.Parked } ? t : null;
    }

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
            Deliver(u, e, m);
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

    // Deliver takes miner u's gold to its side's truck parked at m, else on foot to the nearest bank (none: it waits).
    private void Deliver(Unit u, EconState e, Unit m)
    {
        if (ParkedTruck(m) is { } t && t.Owner == u.Owner && t.Def.Archetype.Haul is { } hs && t.Econ!.Carry < hs.Capacity)
        {
            // Handed from its work spot into the truck on the loading spot (the mine's own carts and chutes; no walk: decision 1039).
            u.ClearPath();
            var put = Math.Min(e.Carry, hs.Capacity - t.Econ.Carry);
            t.Econ.Carry += put;
            e.Carry -= put; // what didn't fit it keeps: it mines on to a load again, for the next truck
            e.Stage = EconStage.None;
            _economyEvents.Add(new EconomyEvent(EconomyEventKind.LoadDropped, u.Owner, u.Id, t.Id, put));
            return;
        }
        if (HaulsFrom(m, u.Owner))
        {
            u.ClearPath();
            e.Stage = EconStage.Waiting; // its side's truck hauls from here (away, or full and leaving): it waits for it with its load
            return;
        }
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
            (e.Mine, e.Stage, e.Told) = (m.Id, EconStage.None, false);
        }
        if (e.Stage is EconStage.ToBank or EconStage.Unloading)
        {
            ToBank(t, e, spec);
            return;
        }
        var dry = me.GoldLeft == 0;
        var lp = LoadPoint(m);
        if (ParkedTruck(m) is { } held && held != t)
        {
            if (e.Carry >= spec.Capacity || (dry && e.Carry > 0))
            {
                ToBank(t, e, spec);
                return;
            }
            if (dry)
            {
                MoveOn(t, e, m);
                return;
            }
            e.Stage = EconStage.Queued; // another truck holds the spot: it waits beside the mine
            Approach(t, lp, HaulQueueReach);
            return;
        }
        if (e.Stage == EconStage.Parked)
        {
            t.ClearPath();
            var leave = e.Carry >= spec.Capacity || (dry && !MinersCarrying(m, t.Owner));
            if (!leave)
            {
                return; // taking loads
            }
            if (e.Carry > 0 && NearestBank(t.Owner, t.Pos) is null)
            {
                Wait(t, e); // nowhere to take it: it stays on the spot, full (loads wait too)
                e.Stage = EconStage.Parked;
                return;
            }
            me.Loader = 0;
            e.Told = false;
            if (e.Carry > 0)
            {
                ToBank(t, e, spec);
                return;
            }
            MoveOn(t, e, m);
            return;
        }
        if (e.Carry >= spec.Capacity || (dry && e.Carry > 0))
        {
            ToBank(t, e, spec);
            return;
        }
        if (dry)
        {
            MoveOn(t, e, m);
            return;
        }
        if (!Within(t.Pos, lp, HaulParkReach))
        {
            e.Stage = EconStage.ToMine;
            Approach(t, lp, HaulParkReach);
            return;
        }
        t.ClearPath();
        e.Stage = EconStage.Parked;
        me.Loader = t.Id;
    }

    // HaulsFrom: whether one of player's trucks is on a Haul order at mine m (its miners wait for it rather than walk; decision 1038).
    private bool HaulsFrom(Unit m, byte player)
    {
        foreach (var t in _units)
        {
            if (t.Owner == player && t.Alive && t.Order.Kind == OrderKind.Haul && t.Order.Target == m.Id)
            {
                return true;
            }
        }
        return false;
    }

    // MinersCarrying: whether player's miners on mine m still carry gold (a dry mine's truck waits for their last loads).
    private bool MinersCarrying(Unit m, byte player)
    {
        foreach (var u in _units)
        {
            if (u.Owner == player && u.Alive && u.Order.Kind == OrderKind.Mine && u.Order.Target == m.Id && u.Econ is { Carry: > 0 })
            {
                return true;
            }
        }
        return false;
    }

    // ToBank drives truck t's gold to the nearest bank's unload spot and unloads it there (spec.UnloadTicks), then sends it back.
    private void ToBank(Unit t, EconState e, HaulSpec spec)
    {
        if (e.Stage == EconStage.Unloading && Unit(e.At) is { } at && at.Def.Archetype.Bank is not null && at.Owner == t.Owner && at.Alive &&
            Within(t.Pos, UnloadPoint(at), BankSpotReach))
        {
            t.ClearPath();
            if (--e.Left <= 0)
            {
                Bank(t, at, e); // back to the mine next tick (Stage None)
            }
            return;
        }
        if (NearestBank(t.Owner, t.Pos) is not { } bank)
        {
            Wait(t, e);
            e.Stage = EconStage.ToBank;
            return;
        }
        e.Told = false;
        var up = UnloadPoint(bank);
        if (!Within(t.Pos, up, BankSpotReach))
        {
            e.Stage = EconStage.ToBank;
            Approach(t, up, BankSpotReach);
            return;
        }
        t.ClearPath();
        (e.Stage, e.At, e.Left) = (EconStage.Unloading, bank.Id, spec.UnloadTicks);
    }

    // MoveOn sends empty truck t on from dry mine m: to the nearest other mine with gold its side's miners work, or home to the
    // nearest bank (none: it idles), with an event either way.
    private void MoveOn(Unit t, EconState e, Unit m)
    {
        Unit? next = null;
        long bd = long.MaxValue;
        foreach (var u in _units)
        {
            if (u.Owner == t.Owner && u.Alive && u.Order.Kind == OrderKind.Mine && u.Order.Target != m.Id &&
                Unit(u.Order.Target) is { Econ.GoldLeft: > 0 } o && o.Def.Archetype.Mine is not null && (o.Pos - t.Pos).LenSq() is var d &&
                (d < bd || (d == bd && o.Id < next!.Id)))
            {
                (next, bd) = (o, d);
            }
        }
        _economyEvents.Add(new EconomyEvent(EconomyEventKind.HaulMoved, t.Owner, t.Id, next?.Id ?? 0, 0));
        (e.Stage, e.Told) = (EconStage.None, false);
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
