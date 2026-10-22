using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The airlifter's orders (logistics, 2026-10-22; docs/design.md, "Logistics"). Generic: any aircraft with a weapon carrying an
// airdrop block (cargo pallets) takes them.
//  - Airdrop (command 38): an attack-ground order on the drop zone's centre flown by the strike system (AirStrikeSystem.cs and
//    AttackPlanSystem.cs: the attack-run state machine, fire modes all4 / single, the level release solution at the pallets'
//    release height, scatter); Unit.AirdropAt marks it, and only such an order drops pallets (an ordinary attack order never
//    picks them, nor Guard). A pallet falls under its parachute (the bomb's integer fall with its retard) and lands: inside a
//    drop zone of its side its supply goes into that store's stock (what does not fit is lost), outside every zone it is lost
//    (AirdropLand; events in World.LogisticsEvents). With every pallet gone it goes home on its own to load again (decision 143's
//    rule), preferring an airfield whose supply is unlimited (a main air base): only there are pallets loaded.
//  - Deliver (command 39): it goes home to the target airfield (one whose core holds a limited stock: the forward airbase) for
//    HomeReason.Deliver, lands, taxis to a stand and there (StayParked) unloads its pallets into the stock one every
//    unloadTicks (whole pallets that fit; the rest stay aboard), takes on fuel (paid from the stock as any service there), and
//    leaves: once it flies free it goes home to load again (AirOps.RecallTo: the nearest airfield of its side with unlimited
//    supply that can take it).
//  - Shot down with pallets aboard, their cargo is lost (a CargoLost event; LossSystem).
public sealed partial class World
{
    /// <summary>Aircraft u's cargo pallets: its first weapon with an airdrop block, or null.</summary>
    public static WeaponDef? AirdropWeapon(Unit u)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Effect.Airdrop is not null)
            {
                return weapons[i];
            }
        }
        return null;
    }

    /// <summary>
    /// The cargo aboard aircraft u (logistics): its pallets aboard and the supply they carry; (0, 0) without pallets. For the
    /// aircraft card.
    /// </summary>
    public static (int Pallets, int Supply) CargoAboard(Unit u) =>
        AirdropWeapon(u) is { Stock: >= 0, Effect.Airdrop: { } ad } w ? (u.Charges[w.Stock], u.Charges[w.Stock] * ad.Amount) : (0, 0);

    // AirdropCommand runs an Airdrop command: each listed aircraft of the player with pallets aboard flies an attack-ground pass at
    // the centre of the target's drop zone (the strike system), marked as an airdrop; parked without pallets where they are loaded,
    // it loads first (an implicit Rearm, as an attack order's, D-ORD2). Refusals as an attack order's.
    private void AirdropCommand(Command c)
    {
        if (Unit(c.Target) is not { } pad || pad.Owner != c.Player || !pad.Alive || !pad.IsBuilt || DropZoneOf(pad) is not { } zone ||
            pad.Def.Supply is not { Stock: > 0 })
        {
            return; // not a finished drop zone of the player's
        }
        var cell = Cell.Of(zone.Centre);
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (!Commandable(u, c.Player) || u!.Def.Archetype.Flight is null || AirdropWeapon(u) is not { } w)
            {
                continue;
            }
            if (!HasAmmo(u, w))
            {
                if (DockedLoad(u))
                {
                    Refuse(u, OrderRefusalReason.Rearming, pad.Id, cell); // loading first, then the airdrop
                    Assign(u, new Order(OrderKind.AttackGround, cell, 0, 0));
                    u.AirdropAt = pad.Id;
                }
                else
                {
                    Refuse(u, OrderRefusalReason.NoAmmo, pad.Id, cell);
                }
                continue;
            }
            if (AirOrderRefusal(u) is { } why)
            {
                Refuse(u, why, pad.Id, cell); // homing: it lands first
                continue;
            }
            if (!FuelFor(u, zone.Centre))
            {
                Refuse(u, OrderRefusalReason.Bingo, pad.Id, cell);
                continue;
            }
            Assign(u, new Order(OrderKind.AttackGround, cell, 0, 0));
            u.AirdropAt = pad.Id;
        }
    }

    // DockedLoad: aircraft u is on the ground (landed to parked) at an airfield that loads pallets (unlimited supply): it gets the
    // full service there (a Rearm visit), then leaves on its order.
    private bool DockedLoad(Unit u)
    {
        if (u.AirOps is not { Phase: >= FlightPhase.Rollout and <= FlightPhase.Parked } ops || Airfield(ops.Airfield) is not { } af || AirfieldStore(af) is not null)
        {
            return false;
        }
        (u.HomeReason, ops.Hold, ops.Launch) = (HomeReason.Rearm, false, false);
        return true;
    }

    // AirdropLand lands pallet p (weapon airdrop ad) at `at`: inside a drop zone of its side (the first store by ID whose zone holds
    // the point) its supply goes into that store's stock up to its cap, the rest lost; outside every zone all of it is lost.
    private void AirdropLand(Projectile p, Vec at, Airdrop ad)
    {
        foreach (var s in _units)
        {
            if (s.Owner != p.Owner || !s.Alive || !s.IsBuilt || s.Def.Supply is not { Stock: > 0 } sup || DropZoneOf(s) is not { } z ||
                !Within(z.Centre, at, z.Radius))
            {
                continue;
            }
            var add = Math.Clamp(sup.Stock - s.Stock, 0, ad.Amount);
            s.Stock += add;
            _logisticsEvents.Add(new LogisticsEvent(LogisticsEventKind.PalletLanded, p.Shooter, s.Id, at, add, ad.Amount - add, p.Id));
            return;
        }
        _logisticsEvents.Add(new LogisticsEvent(LogisticsEventKind.PalletLost, p.Shooter, 0, at, 0, ad.Amount, p.Id));
    }

    // AirdropOrderEnds: u's airdrop order is no longer valid (its pad gone, or no longer the player's): the strike ends.
    private bool AirdropPadGone(Unit u) =>
        u.AirdropAt != 0 && !(Unit(u.AirdropAt) is { Alive: true } pad && pad.Owner == u.Owner);

    // ---- Deliver ----

    // DeliverCommand runs a Deliver command: each listed aircraft of the player with pallets aboard goes to the target's airfield
    // (one with a limited stock that can take it) to land and unload there: flying free at once; parked or taxiing at another
    // airfield, once it flies free (it leaves now); holding at another airfield, it moves there.
    private void DeliverCommand(Command c)
    {
        if (Unit(c.Target) is not { } tu || tu.Owner != c.Player || AirfieldOf(tu) is not { } to || AirfieldStore(to) is null)
        {
            return; // not an airfield of the player's with a stock
        }
        foreach (var id in c.Units)
        {
            if (Unit(id) is not { Alive: true, Inside: 0 } u || u.Owner != c.Player || u.Def.Archetype.Aircraft is not { Landing: LandingKind.Runway } ac ||
                CargoAboard(u).Pallets == 0 || !to.Type.Serves(ac.SizeClass) || !RunwayFor(to, ac, out _))
            {
                continue;
            }
            switch (u.AirOps)
            {
                case null:
                    GoHome(u, to, HomeReason.Deliver);
                    break;
                case { Phase: FlightPhase.Holding } ops when ops.Airfield != to.Id:
                    if (Airfield(ops.Airfield) is { } from)
                    {
                        LeaveAirfield(u, from, ops);
                    }
                    MoveTo(u, to, ac, ops);
                    (u.HomeReason, u.AirOps!.Hold) = (HomeReason.Deliver, false);
                    break;
                case { } ops when ops.Airfield == to.Id && ops.Phase is >= FlightPhase.Holding and <= FlightPhase.Parked:
                    (u.HomeReason, ops.Hold, ops.Launch) = (HomeReason.Deliver, false, false); // landing or parked there: it unloads here
                    break;
                case { } ops:
                    // Elsewhere on the ground or climbing out: it leaves (or flies on) and turns for the target once it flies free.
                    u.Idle();
                    (ops.Recall, ops.RecallReason, ops.RecallTo) = (true, HomeReason.Deliver, to.Id);
                    if (ops.Phase is >= FlightPhase.Rollout and <= FlightPhase.Parked)
                    {
                        (ops.Launch, ops.Hold) = (true, false);
                    }
                    break;
            }
        }
    }

    // DeliverParked runs a tick of parked aircraft u's delivery at airfield af (HomeReason.Deliver, at a station of a field with a
    // stock): one pallet into the stock every unloadTicks while one fits; then it is done (it leaves once refuelled, to load
    // again). Returns true while it is still unloading.
    private bool DeliverParked(Unit u, Airfield af, AirOps ops)
    {
        var w = AirdropWeapon(u);
        var store = AirfieldStore(af);
        if (w is not { Stock: >= 0, Effect.Airdrop: { } ad } || store is null || ops.Station == 0)
        {
            return EndDelivery(u, ops);
        }
        var aboard = u.Charges[w.Stock];
        if (aboard == 0 || store.Def.Supply!.Stock - store.Stock < ad.Amount)
        {
            return EndDelivery(u, ops); // all unloaded, or the stock has no room for another (the rest stays aboard)
        }
        if (ops.UnloadLeft == 0)
        {
            ops.UnloadLeft = ad.UnloadTicks;
        }
        if (--ops.UnloadLeft > 0)
        {
            return true;
        }
        u.MutableCharges[w.Stock] = aboard - 1;
        store.Stock += ad.Amount;
        _logisticsEvents.Add(new LogisticsEvent(LogisticsEventKind.Unloaded, u.Id, store.Id, u.Pos, ad.Amount, 0, 0));
        return true;
    }

    // EndDelivery ends u's delivery: it leaves once refuelled and, flying free, goes home to load again (the nearest airfield of its
    // side with unlimited supply that can take it; none: its best home).
    private bool EndDelivery(Unit u, AirOps ops)
    {
        ops.UnloadLeft = 0;
        u.HomeReason = HomeReason.Refuel; // refuelled here (paid from the stock), not rearmed
        var home = u.Def.Archetype.Aircraft is { } ac ? LoadingAirfield(u, ac) : null;
        (ops.Recall, ops.RecallReason, ops.RecallTo, ops.Hold) = (true, HomeReason.Rearm, home?.Id ?? 0, false);
        return false;
    }

    // LoadingAirfield is the nearest airfield of u's side (to it) whose supply is unlimited (no limited stock: a main air base) and
    // that can take it (its class, its runway and pattern), or null.
    private Airfield? LoadingAirfield(Unit u, AircraftSpec ac)
    {
        Airfield? best = null;
        long bestD = 0;
        foreach (var af in _airfields)
        {
            if (af.Owner != u.Owner || AirfieldStore(af) is not null || !af.HasRunway || !af.Type.Serves(ac.SizeClass) || !PatternFits(af, ac) ||
                !RunwayFor(af, ac, out var rw))
            {
                continue;
            }
            var d = (HoldPoint(af, rw) - u.Pos).LenSq();
            if (best is null || d < bestD)
            {
                (best, bestD) = (af, d);
            }
        }
        return best;
    }

    // CargoShort: u carries pallets and some are gone (it wants to load: only at an airfield with unlimited supply).
    private static bool CargoShort(Unit u) => AirdropWeapon(u) is { Stock: >= 0 } w && u.Charges[w.Stock] < u.Def.StockMax[w.Stock];

    // CargoLost reports u's pallets lost with it (it died with some aboard).
    private void CargoLost(Unit u)
    {
        if (CargoAboard(u) is { Pallets: > 0 } aboard)
        {
            _logisticsEvents.Add(new LogisticsEvent(LogisticsEventKind.CargoLost, u.Id, 0, u.Pos, 0, aboard.Supply, 0));
        }
    }
}
