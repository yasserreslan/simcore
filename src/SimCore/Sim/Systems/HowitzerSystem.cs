using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>What an <see cref="EmplacementEvent"/> reports (the howitzer; docs/design.md, "Howitzer").</summary>
public enum EmplacementEventKind : byte
{
    /// <summary>
    /// It fired a round (<see cref="EmplacementEvent.Other"/>: the unit aimed at, 0 at a point; <see cref="EmplacementEvent.At"/>: the
    /// aim point after scatter; <see cref="EmplacementEvent.Value"/>: rounds left; <see cref="EmplacementEvent.Value2"/>: the
    /// projectile's ID, 0 for an instant shot).
    /// </summary>
    Fired,

    /// <summary>
    /// A shot's flash shows it to every enemy (<see cref="EmplacementEvent.Value"/>: ticks it stays shown, from this tick;
    /// <see cref="EmplacementEvent.Value2"/>: 1 when it was hidden until this shot, 0 when the shot only started the time again).
    /// </summary>
    Revealed,

    /// <summary>
    /// A cargo truck handed it a round (<see cref="EmplacementEvent.Other"/>: the truck; <see cref="EmplacementEvent.Value"/>: rounds
    /// now; <see cref="EmplacementEvent.Value2"/>: the truck's supply left).
    /// </summary>
    RoundIn,

    /// <summary>
    /// A truck's handover ended (<see cref="EmplacementEvent.Other"/>: the truck; <see cref="EmplacementEvent.Value"/>: rounds now;
    /// <see cref="EmplacementEvent.Value2"/>: the truck's supply left): the gun is full, the truck is short of a round, or it left.
    /// </summary>
    ReloadEnded,

    /// <summary>A soldier took its crew seat (<see cref="EmplacementEvent.Other"/>: the soldier).</summary>
    CrewIn,

    /// <summary>Its crewman got out on an Exit order (<see cref="EmplacementEvent.Other"/>: the soldier).</summary>
    CrewOut,

    /// <summary>
    /// It was destroyed with a crewman in the seat, who bailed out (<see cref="EmplacementEvent.Other"/>: the soldier;
    /// <see cref="EmplacementEvent.Value"/>: 1 he lived, 0 he died).
    /// </summary>
    CrewBailed,

    /// <summary>Its crewman was killed in the seat by a hit on the gun (<see cref="EmplacementEvent.Other"/>): it falls silent.</summary>
    CrewLost,
}

/// <summary>Something that happened to a crewed emplacement this tick (the howitzer), for the client: <see cref="World.EmplacementEvents"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Unit">The emplacement (the gun).</param>
/// <param name="Other">The other unit involved (by kind), or 0.</param>
/// <param name="At">Where: the aim point for a shot, else the gun's position.</param>
/// <param name="Value">A count (by kind).</param>
/// <param name="Value2">A second count (by kind).</param>
public readonly record struct EmplacementEvent(EmplacementEventKind Kind, uint Unit, uint Other, Vec At, int Value, int Value2);

/// <summary>
/// The state of a gun that cargo trucks reload, for its selection card (<see cref="World.EmplacementOf"/>).
/// </summary>
/// <param name="Rounds">Rounds aboard (shells).</param>
/// <param name="MaxRounds">Rounds when full.</param>
/// <param name="Crew">Its crewman's ID, or 0 (silent).</param>
/// <param name="RevealLeft">Ticks its last shot still shows it to the enemy (0: hidden).</param>
/// <param name="RevealUntil">The first tick it is no longer shown (0: it never fired).</param>
/// <param name="Truck">The cargo truck handing it rounds now, or 0.</param>
/// <param name="NextRound">Ticks until that truck's next round goes across (0 without one).</param>
/// <param name="ReloadLeft">Ticks until it can fire again (0: ready).</param>
public readonly record struct EmplacementStatus(int Rounds, int MaxRounds, uint Crew, int RevealLeft, uint RevealUntil, uint Truck, int NextRound,
    int ReloadLeft)
{
    /// <summary>Whether a crewman is in the seat (it can fire).</summary>
    public bool Manned => Crew != 0;

    /// <summary>Whether its last shot still shows it to every enemy.</summary>
    public bool Revealed => RevealLeft > 0;
}

// The howitzer's systems (docs/design.md, "Howitzer"; owner's spec 2026-10-24), each generic and driven by content:
//  - the muzzle flash (weapon reveal): every shot shows its shooter to every enemy player for the weapon's reveal ticks, spotted
//    through the fog and targetable as if seen (RunReveal, in the vision system, after spotting);
//  - the truck reload (weapon truckReload; RunTruckReload, in the cargo system before depot unloads): a cargo truck of the gun's
//    side with supply aboard, sent to it (Unload) or standing still and idle within the weapon's range of its footprint, hands it
//    one round every everyTicks for cost supply out of its load until the gun is full or the truck is short of a round; the
//    truck keeps the rest (a partial transfer) and can unload it at a depot or reload another gun. One truck per gun at a time;
//    a truck serves one gun at a time (the one it was sent to, else the nearest that needs rounds, then the lowest ID);
//  - the events and queries the client draws from.
// Crewing (a crew seat on a structure) is the crew system's (CrewSystem.cs), firing the combat system's (CombatSystem.cs).
public sealed partial class World
{
    private readonly List<EmplacementEvent> _emplacementEvents = [];
    private readonly List<Unit> _receivers = []; // scratch: this tick's guns that trucks reload, in ID order (not state)

    // Flashes: a weapon of def gives its shooter away when it fires (reveal).
    private static bool Flashes(UnitDef def)
    {
        for (var i = 0; i < def.Weapons.Count; i++)
        {
            if (def.Weapons[i].Effect.RevealTicks > 0)
            {
                return true;
            }
        }
        return false;
    }

    // Whether any unit ever added has a gun that trucks reload, or a weapon that flashes; while none has, those passes cost
    // nothing. Not hashed (derived from the units).
    private bool _truckReloads, _reveals;

    /// <summary>What happened to the crewed emplacements this tick (shots, flashes, truck rounds, crew in and out), in order.</summary>
    public IReadOnlyList<EmplacementEvent> EmplacementEvents => _emplacementEvents;

    private void ClearEmplacementEvents() => _emplacementEvents.Clear();

    /// <summary>
    /// The state of u's gun that cargo trucks reload (rounds, crew, flash, the truck feeding it), or null when u has no such weapon.
    /// </summary>
    public EmplacementStatus? EmplacementOf(Unit u)
    {
        if (u.Def.TruckReloadWeapon < 0)
        {
            return null;
        }
        var w = u.Def.Weapons[u.Def.TruckReloadWeapon];
        uint truck = 0;
        var next = 0;
        foreach (var t in _units)
        {
            if (t.FeedTo == u.Id)
            {
                (truck, next) = (t.Id, t.FeedLeft);
                break;
            }
        }
        return new EmplacementStatus(u.Charges[w.Stock], u.StockMax(w.Stock), u.Def.Crew is null ? 0 : u.Driver, RevealLeft(u), u.RevealUntil,
            truck, next, (u.Reload + 99) / 100);
    }

    /// <summary>Ticks u's last flashing shot still shows it to every enemy (0: it is hidden as usual).</summary>
    public int RevealLeft(Unit u) => u.RevealUntil > Tick ? (int)(u.RevealUntil - Tick) : 0;

    /// <summary>Whether u's last shot shows it to player now (an enemy of its owner): the flash marker on that player's map.</summary>
    public bool RevealedBy(Unit u, byte player) => player != u.Owner && player != 0 && u.Owner != 0 && u.RevealUntil > Tick && u.Alive && u.Inside == 0;

    /// <summary>The gun truck t is handing rounds to now, or 0.</summary>
    public uint FeedingOf(Unit t) => t.FeedTo;

    /// <summary>Whether t (a cargo truck) could hand gun g a round now: g is its side's finished gun that needs one and t has a round's supply.</summary>
    public bool CanFeed(Unit t, Unit g) =>
        g.Def.TruckReloadWeapon >= 0 && TruckReloadOf(g) is { } tr && t.Def.Cargo is not null && t.Alive && g.Alive && g.IsBuilt &&
        g.Owner == t.Owner && t.Cargo >= tr.Cost && NeedsRound(g);

    private static TruckReload? TruckReloadOf(Unit g) => g.Def.TruckReloadWeapon >= 0 ? g.Def.Weapons[g.Def.TruckReloadWeapon].Effect.TruckReload : null;

    private static bool NeedsRound(Unit g)
    {
        var w = g.Def.Weapons[g.Def.TruckReloadWeapon];
        return g.Charges[w.Stock] < g.StockMax(w.Stock);
    }

    // InFeedRange: truck t stands within the truck reload's range of gun g's footprint (its position for a unit).
    private static bool InFeedRange(Unit t, Unit g, TruckReload tr) => Within(t.Pos, AimPoint(t.Pos, g), tr.Range);

    // NoteShot runs for every round a weapon fires (salvo shots, projectiles, instant hits): the muzzle flash (reveal) and the
    // Fired event for guns that flash or that trucks reload.
    private void NoteShot(Unit u, WeaponDef w, uint target, Vec at, uint projectile)
    {
        var e = w.Effect;
        if (e.RevealTicks <= 0 && e.TruckReload is null)
        {
            return;
        }
        var left = w.Stock >= 0 ? u.Charges[w.Stock] : -1;
        _emplacementEvents.Add(new EmplacementEvent(EmplacementEventKind.Fired, u.Id, target, at, left, (int)projectile));
        if (e.RevealTicks > 0)
        {
            var fresh = u.RevealUntil <= Tick;
            u.RevealUntil = Tick + 1 + (uint)e.RevealTicks; // spotted by the next RevealTicks vision passes (vision ran before combat)
            _emplacementEvents.Add(new EmplacementEvent(EmplacementEventKind.Revealed, u.Id, 0, u.Pos, e.RevealTicks, fresh ? 1 : 0));
        }
    }

    // RunReveal (the vision system, after spotting): every unit a flashing shot still shows is spotted by every enemy player,
    // through the fog, as if seen. It only ORs bits, in ID order.
    private void RunReveal()
    {
        if (!_reveals)
        {
            return;
        }
        var players = _players.Count;
        foreach (var u in _units)
        {
            if (u.RevealUntil <= Tick || !u.Alive || u.Inside != 0 || u.Owner == 0)
            {
                continue;
            }
            for (var p = 1; p <= players; p++)
            {
                if (p != u.Owner)
                {
                    u.SpottedBy |= Bit((byte)p);
                }
            }
        }
    }

    // RunTruckReload (the cargo system, before depot unloads): each cargo truck, in ID order, hands a gun its rounds. See the
    // file's header for the rules.
    private void RunTruckReload()
    {
        if (!_truckReloads)
        {
            return; // no gun that trucks reload was ever on the map: no truck can be feeding one
        }
        _receivers.Clear();
        foreach (var g in _units)
        {
            if (g.Def.TruckReloadWeapon >= 0 && g.Alive && g.IsBuilt && g.Owner != 0)
            {
                _receivers.Add(g);
            }
        }
        foreach (var t in _units)
        {
            if (t.Def.Cargo is null)
            {
                continue;
            }
            var g = _receivers.Count == 0 ? null : FeedTarget(t);
            if (g is null)
            {
                if (t.FeedTo != 0)
                {
                    EndFeed(t, Unit(t.FeedTo));
                }
                continue;
            }
            var tr = TruckReloadOf(g)!;
            if (t.FeedTo != g.Id)
            {
                if (t.FeedTo != 0)
                {
                    EndFeed(t, Unit(t.FeedTo));
                }
                (t.FeedTo, t.FeedLeft) = (g.Id, tr.EveryTicks);
            }
            if (--t.FeedLeft > 0)
            {
                continue;
            }
            var w = g.Def.Weapons[g.Def.TruckReloadWeapon];
            g.MutableCharges[w.Stock]++;
            t.Cargo -= tr.Cost;
            _emplacementEvents.Add(new EmplacementEvent(EmplacementEventKind.RoundIn, g.Id, t.Id, g.Pos, g.Charges[w.Stock], t.Cargo));
            if (NeedsRound(g) && t.Cargo >= tr.Cost)
            {
                t.FeedLeft = tr.EveryTicks;
                continue;
            }
            EndFeed(t, g);
            if (t.Order.Kind == OrderKind.Unload && t.Order.Target == g.Id)
            {
                t.Idle(); // done: the gun is full or the truck is short of a round; it keeps the rest
            }
        }
    }

    // EndFeed ends truck t's handover to gun g (gone or not): the event, and the truck free again.
    private void EndFeed(Unit t, Unit? g)
    {
        if (g is not null && g.Def.TruckReloadWeapon >= 0)
        {
            var w = g.Def.Weapons[g.Def.TruckReloadWeapon];
            _emplacementEvents.Add(new EmplacementEvent(EmplacementEventKind.ReloadEnded, g.Id, t.Id, g.Pos, g.Charges[w.Stock], t.Cargo));
        }
        (t.FeedTo, t.FeedLeft) = (0, 0);
    }

    // FeedTarget is the gun truck t hands rounds to this tick, or null: the truck must stand still, idle (or on an Unload order
    // to a gun), out of any depot unload, with a round's supply; the gun must be its side's, finished, short of rounds, within
    // the reload's range and fed by no other truck. A truck sent to a gun serves only that one; an idle one keeps the gun it
    // feeds, else takes the nearest (to its footprint) that qualifies, then the lowest ID.
    private Unit? FeedTarget(Unit t)
    {
        if (!t.Alive || t.Inside != 0 || t.Moving || t.PathPending || t.Cargo <= 0 || t.UnloadLeft > 0)
        {
            return null;
        }
        if (t.Order.Kind == OrderKind.Unload)
        {
            return Unit(t.Order.Target) is { } sent && sent.Def.TruckReloadWeapon >= 0 && Feedable(t, sent) ? sent : null;
        }
        if (t.Order.Kind != OrderKind.Idle)
        {
            return null;
        }
        if (Unit(t.FeedTo) is { } cur && Feedable(t, cur))
        {
            return cur;
        }
        Unit? best = null;
        long bestD = 0;
        foreach (var g in _receivers)
        {
            if (!Feedable(t, g))
            {
                continue;
            }
            var d = (AimPoint(t.Pos, g) - t.Pos).LenSq();
            if (best is null || d < bestD)
            {
                (best, bestD) = (g, d); // ID order: ties keep the lower ID
            }
        }
        return best;
    }

    // Feedable: truck t may hand gun g a round from where it stands, and no other truck feeds g.
    private bool Feedable(Unit t, Unit g)
    {
        if (!CanFeed(t, g) || !InFeedRange(t, g, TruckReloadOf(g)!))
        {
            return false;
        }
        foreach (var o in _units)
        {
            if (o != t && o.FeedTo == g.Id && o.Alive)
            {
                return false;
            }
        }
        return true;
    }
}
