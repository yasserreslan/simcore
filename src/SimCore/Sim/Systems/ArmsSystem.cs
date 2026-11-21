using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Rng;

namespace SimCore.Sim;

// Infantry arms (INF-E1a, docs/design.md "Infantry weapons and ammo"; the game repo's docs/plans/infantry-combat-brief.md, 6.5
// and 6.9): a soldier's weapons with carried ammo (Carried: its stock, the magazine's rounds included) fire from a magazine; an
// empty magazine is replaced in MagazineReload ticks (alongside the shot's own reload), started on the shot that empties it. An
// idle soldier with a partial magazine and no target for 3 s tops it up (not when low on ammo: under 25% of the main arm's load).
// A soldier with a sidearm (UnitDef.Sidearm) holds one arm at a time: he draws the sidearm (its DrawTicks, the same back) when
// the main arm is empty, when spotted enemy infantry comes within the main arm's SwitchWithin, or within its SwitchReloadingWithin
// while its magazine is replaced; he goes back once no enemy infantry has been within that distance plus a cell for 2 s and the
// main arm has ammo. No weapon fires while he switches. A player's primaryOnly weapon mode keeps the main arm in hand. With
// nothing left and no order he walks to the nearest supply point of his side within 40 cells. Supply points (any supplier that
// rearms launchers: the supply depot, the field ammo point, the supply stash, the crates) refill soldiers within 2 cells of them,
// one magazine or one rocket every 10 ticks, paid from their stock (RunInfantryResupply). Units are visited in ID order; all
// integer.
public sealed partial class World
{
    private const int TopUpTicks = 60; // 3 s with no target and no shot before an idle soldier tops up his magazine
    private const int CalmTicks = 40; // 2 s with no enemy infantry near before he goes back to his main arm
    private const int CalmMax = 1000; // the calm count stops here (hashed state stays small)
    private const int LowAmmoPercent = 25; // under this share of the main arm's load he is low on ammo
    private const int ResupplyEvery = 10; // a magazine or a rocket every 0.5 s at a supply point
    private const int ResupplyCheckEvery = 20; // an empty soldier with no order looks for a supply point once a second
    private static readonly Fixed ResupplyReach = Fixed.FromInt(2); // within 2 cells of a supply point (its footprint)
    private static readonly Fixed ResupplySearch = Fixed.FromInt(40); // the farthest supply point an empty soldier walks to
    private const ulong HitRollSalt = 0x41C0_0000_0000_0000UL; // keeps the small-arms hit roll apart from other rolls on the same IDs
    private const ulong MissPointSalt = 0x3155_0000_0000_0000UL; // ENG-POLISH: and a miss's impact point apart from both
    private const long MissNear = Fixed.OneRaw / 4; // a miss lands at least a quarter of a cell off the target on one axis
    private const long MissFar = Fixed.OneRaw * 3 / 4; // and at most three quarters of a cell on each: in its cell's neighbourhood

    private readonly List<Unit> _nearArms = []; // scratch, not state
    private readonly List<ShotEvent> _shotEvents = []; // output, not state: cleared every tick

    /// <summary>
    /// The last tick's small-arms shots (ENG-POLISH): every instant shot of a weapon with an accuracy (the ones that roll to hit),
    /// hit or miss, in the order fired (units by ID). The client's tracers and impacts; near misses for suppression.
    /// </summary>
    public IReadOnlyList<ShotEvent> ShotEvents => _shotEvents;

    // Test oracle: every shot hits, as before the hit roll (the test arena's default, so tests of other mechanics keep their exact
    // timings; the hit roll's own tests and every real match leave it off). Never set in a match.
    internal bool SureHits { get; set; }

    /// <summary>
    /// Whether soldier u is low on ammo (INF-E1a): his main arm (the first weapon) carries ammo and has under 25% of its load left.
    /// He then never tops up a partial magazine.
    /// </summary>
    public static bool LowAmmo(Unit u)
    {
        var weapons = u.Def.Weapons;
        if (weapons.Count == 0 || weapons[0] is not { Stock: >= 0, Effect.Carried: > 0 } w)
        {
            return false;
        }
        return (long)u.Charges[w.Stock] * 100 < (long)u.StockMax(w.Stock) * LowAmmoPercent;
    }

    // HoldsArm: weapon index wi of u may fire now as far as its arms go: not switching, and with a sidearm, the arm in its hands.
    internal static bool HoldsArm(Unit u, int wi) =>
        u.Arms is not { } a || u.Def.Sidearm < 0 || (!a.Switching && a.InHand == wi);

    // MagazineReady: u's weapon wi has a round in its magazine and is not being reloaded (true for weapons without a magazine).
    internal static bool MagazineReady(Unit u, WeaponDef w) =>
        u.Arms is not { } a || w.Effect.Magazine == 0 || (a.Mag[w.Index] > 0 && a.Reloading != w.Index);

    // SpendRound takes one round out of u's magazine of w, and starts replacing the magazine on the shot that empties it.
    internal void SpendMagazineRound(Unit u, WeaponDef w)
    {
        if (u.Arms is not { } a || w.Effect.Magazine == 0)
        {
            return;
        }
        var mag = a.MutableMag;
        mag[w.Index] = Math.Max(0, mag[w.Index] - 1);
        if (mag[w.Index] == 0 && a.Reloading < 0 && w.Stock >= 0 && u.Charges[w.Stock] > 0)
        {
            (a.Reloading, a.ReloadLeft) = (w.Index, MagazineTicks(u, w)); // INF-E2a: a rank reloads faster
        }
    }

    // MagazineTicks: how long u takes to replace w's magazine (INF-E2a: a rank's bonus, SoldierSystem.cs).
    private int MagazineTicks(Unit u, WeaponDef w) =>
        u.Life is null ? w.Effect.MagazineReload : Math.Max(1, (int)((long)w.Effect.MagazineReload * ReloadPercent(u) / 100));

    // HitRoll: whether an instant shot of w from u hits t (INF-E1a): always without an accuracy or at a structure; otherwise the
    // accuracy up to 60% of the range, falling linearly to 60% of it at full range, against a roll hashed from the seed, the tick
    // and both IDs (fixed whatever the visiting order, and apart from the world's random stream).
    internal bool HitRoll(Unit u, Unit t, WeaponDef w)
    {
        var acc = w.Effect.Accuracy;
        if (acc <= 0 || t.Def.IsStructure || SureHits)
        {
            return true;
        }
        var chance = HitChance(u, t, w);
        var roll = (long)(Rand.Hash(Match.Setup.Seed, Tick, u.Id, t.Id | HitRollSalt) % 1000);
        return roll < chance;
    }

    // HitChance is the per mille chance HitRoll rolls against (a weapon with an accuracy, a unit target): accuracy, rank and
    // wounds, the range fall-off, then posture, cover and suppression (INF-E1b-1).
    internal long HitChance(Unit u, Unit t, WeaponDef w)
    {
        var acc = w.Effect.Accuracy;
        var chance = (long)acc * 10 * AimPercent(u) / 100; // per mille; INF-E2a: rank and wounds (SoldierSystem.cs)
        var range = Math.Max(1L, w.Range.Raw);
        var d = (AimPoint(u.Pos, t) - u.Pos).Len().Raw;
        var near = range * 6 / 10;
        if (d > near)
        {
            chance = chance * Math.Max(0L, range - (d - near)) / range;
        }
        return CombatChance(u, t, chance); // INF-E1b-1: posture, cover, suppression (InfantryCombatSystem.cs)
    }

    // MissPoint: where a missed small-arms round from u at t strikes (ENG-POLISH): t's position moved a quarter to three quarters of
    // a cell on one axis and up to three quarters on the other (so never on t, always within its cell's 3 x 3 neighbourhood), by
    // a hash of the seed, the tick and both IDs like the hit roll (no world random stream, no allocation), kept on the map.
    internal Vec MissPoint(Unit u, Unit t)
    {
        var h = Rand.Hash(Match.Setup.Seed, Tick, u.Id, t.Id | MissPointSalt);
        var span = (ulong)(2 * MissFar + 1);
        var dx = (long)(h % span) - MissFar;
        var dy = (long)((h >> 24) % span) - MissFar;
        if (Math.Abs(dx) < MissNear && Math.Abs(dy) < MissNear)
        {
            var far = (h >> 48) % 2 == 0;
            if (far)
            {
                dx += dx < 0 ? -MissNear : MissNear;
            }
            else
            {
                dy += dy < 0 ? -MissNear : MissNear;
            }
        }
        var x = Math.Clamp(t.Pos.X.Raw + dx, 0, Grid.Width * Fixed.OneRaw - 1);
        var y = Math.Clamp(t.Pos.Y.Raw + dy, 0, Grid.Height * Fixed.OneRaw - 1);
        return new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y));
    }

    // NoteSmallArms records an instant shot of weapon wi from u at t for the client (ShotEvents), when the weapon rolls to hit.
    private void NoteSmallArms(Unit u, int wi, WeaponDef w, Unit t, bool hit, Vec at)
    {
        if (w.Effect.Accuracy > 0)
        {
            _shotEvents.Add(new ShotEvent(u.Id, t.Id, hit, at, wi));
        }
    }

    // RunArms runs every soldier's magazines and arm switch for this tick, before the combat system fires (RunCombat; the
    // spatial index is live).
    private void RunArms()
    {
        foreach (var u in _units)
        {
            if (u.Arms is not { } a || !u.Alive)
            {
                continue;
            }
            var weapons = u.Def.Weapons;
            if (a.Switching && --a.SwitchLeft <= 0)
            {
                (a.InHand, a.SwitchTo, a.SwitchLeft) = (a.SwitchTo, -1, 0);
            }
            if (a.Reloading >= 0 && --a.ReloadLeft <= 0)
            {
                var rw = weapons[a.Reloading];
                a.MutableMag[a.Reloading] = Math.Min(rw.Effect.Magazine, rw.Stock >= 0 ? u.Charges[rw.Stock] : rw.Effect.Magazine);
                (a.Reloading, a.ReloadLeft) = (-1, 0);
            }
            if (!u.CanAct || u.Inside != 0)
            {
                continue;
            }
            StartReload(u, a);
            if (u.Def.Sidearm >= 0 && !a.Switching)
            {
                SwitchArms(u, a);
            }
            if (u.Order.Kind == OrderKind.Idle && (Tick + u.Id) % ResupplyCheckEvery == 0 && OutOfAmmo(u))
            {
                WalkToResupply(u);
            }
        }
    }

    // StartReload replaces the magazine in u's hands when it is empty and rounds are left, or tops up a partial one when u is idle
    // (no target, no attack order, no shot for 3 s) and not low on ammo.
    private void StartReload(Unit u, Arms a)
    {
        if (a.Reloading >= 0 || a.Switching)
        {
            return;
        }
        var w = u.Def.Weapons[a.InHand];
        if (w.Effect.Magazine == 0 || w.Stock < 0)
        {
            return;
        }
        var mag = a.Mag[w.Index];
        var left = u.Charges[w.Stock];
        if (left <= mag || mag >= w.Effect.Magazine)
        {
            return; // nothing more to load, or full
        }
        var idle = u.AutoTarget == 0 && u.Order.Kind != OrderKind.Attack && u.Order.Kind != OrderKind.AttackGround &&
            Tick >= u.LastShot + TopUpTicks && !LowAmmo(u);
        if (mag == 0 || idle)
        {
            (a.Reloading, a.ReloadLeft) = (w.Index, MagazineTicks(u, w)); // INF-E2a: a rank reloads faster
        }
    }

    // SwitchArms starts u's switch between its main arm and its sidearm by the rule (see the file comment).
    private void SwitchArms(Unit u, Arms a)
    {
        var weapons = u.Def.Weapons;
        var main = weapons[0];
        var side = weapons[u.Def.Sidearm];
        if (u.WeaponMode == WeaponMode.PrimaryOnly)
        {
            if (a.InHand != 0)
            {
                (a.SwitchTo, a.SwitchLeft) = (0, side.Effect.DrawTicks); // the player keeps him on the main arm
            }
            return;
        }
        var mainAmmo = HasAmmo(u, main);
        if (a.InHand == 0)
        {
            if (!HasAmmo(u, side))
            {
                return;
            }
            var e = main.Effect;
            if (!mainAmmo || OrderNeedsSidearm(u, main, side) || (e.SwitchWithin.Raw > 0 && EnemyInfantryNear(u, e.SwitchWithin)) ||
                (a.Reloading == 0 && e.SwitchReloadingWithin.Raw > 0 && EnemyInfantryNear(u, e.SwitchReloadingWithin)))
            {
                (a.SwitchTo, a.SwitchLeft, a.Calm) = (u.Def.Sidearm, side.Effect.DrawTicks, 0);
            }
            return;
        }
        var back = Fixed.FromRaw(Math.Max(main.Effect.SwitchWithin.Raw, main.Effect.SwitchReloadingWithin.Raw) + Fixed.OneRaw);
        a.Calm = EnemyInfantryNear(u, back) ? 0 : Math.Min(CalmMax, a.Calm + 1);
        if (mainAmmo && (a.Calm >= CalmTicks || !HasAmmo(u, side)) && (!OrderNeedsSidearm(u, main, side) || !HasAmmo(u, side)))
        {
            (a.SwitchTo, a.SwitchLeft) = (0, side.Effect.DrawTicks);
        }
    }

    // OrderNeedsSidearm: u's order is one only its sidearm can carry out: attack ground with a main arm that cannot fire at the
    // ground, or an attack on a unit its main arm cannot hit and its sidearm can.
    private bool OrderNeedsSidearm(Unit u, WeaponDef main, WeaponDef side) => u.Order.Kind switch
    {
        OrderKind.AttackGround => ((main.Effect.Targets & Layers.Ground) == 0 || main.Effect.Interceptor is not null) &&
            (side.Effect.Targets & Layers.Ground) != 0,
        OrderKind.Attack => Unit(u.Order.Target) is { Alive: true } t && !CanHit(main, t) && CanHit(side, t),
        _ => false,
    };

    // EnemyInfantryNear: a living enemy soldier on foot (not inside anything) that u's side spots is within r of u.
    private bool EnemyInfantryNear(Unit u, Fixed r)
    {
        var rr = r.Raw;
        foreach (var o in Around(u.Pos.X.Raw - rr, u.Pos.Y.Raw - rr, u.Pos.X.Raw + rr, u.Pos.Y.Raw + rr, _nearArms, true))
        {
            if (o.Owner != u.Owner && o.Owner != 0 && o.Alive && o.Inside == 0 && o.Garrison == 0 &&
                o.Def.Archetype.Locomotor == Locomotor.Foot && IsSpotted(o, u.Owner) && Within(u.Pos, o.Pos, r))
            {
                return true;
            }
        }
        return false;
    }

    // OutOfAmmo: every weapon of u with carried ammo is empty (and it has one).
    private static bool OutOfAmmo(Unit u)
    {
        var any = false;
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i] is { Stock: >= 0, Effect.Carried: > 0 } w)
            {
                if (u.Charges[w.Stock] > 0)
                {
                    return false;
                }
                any = true;
            }
        }
        return any;
    }

    // NeedsResupply: some weapon of u with carried ammo is not full.
    private static bool NeedsResupply(Unit u)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i] is { Stock: >= 0, Effect.Carried: > 0 } w && u.Charges[w.Stock] < u.StockMax(w.Stock))
            {
                return true;
            }
        }
        return false;
    }

    // IsResupplyPoint: s is a supply point of u's side that rearms infantry: a built supplier (not a store) that is a structure or
    // a crate, as for launchers (IsDepot), whatever its own radius.
    private static bool IsResupplyPoint(Unit s, Unit u) =>
        s.Owner == u.Owner && s.Alive && s.IsBuilt && s.Inside == 0 && s.Def.Supply is { Storage: false } sup && (s.Def.IsStructure || sup.Crate);

    // AtResupplyPoint: u stands within 2 cells of s (of its footprint).
    private static bool AtResupplyPoint(Unit s, Unit u) => Within(u.Pos, AimPoint(u.Pos, s), ResupplyReach);

    // NearestResupplyPoint returns u's side's supply point nearest u (its footprint) within max (any distance when max is zero),
    // with stock left (or unlimited); ties go to the lower ID. Null if none.
    private Unit? NearestResupplyPoint(Unit u, Fixed max)
    {
        Unit? best = null;
        long bestD = 0;
        foreach (var s in _units) // (rarely: an order, or an empty soldier once a second; the supply list is last tick's)
        {
            if (s.Def.Supply is null || !IsResupplyPoint(s, u) || (s.Def.Supply!.Stock > 0 && s.Stock <= 0))
            {
                continue;
            }
            var d = (AimPoint(u.Pos, s) - u.Pos).LenSq();
            if ((max.Raw > 0 && d > max.Raw * max.Raw) || (best is not null && d >= bestD))
            {
                continue;
            }
            (best, bestD) = (s, d);
        }
        return best;
    }

    // WalkToResupply sends u (empty, with no order) to the nearest supply point within 40 cells, unless it already stands at one.
    private void WalkToResupply(Unit u)
    {
        foreach (var s in _units)
        {
            if (s.Def.Supply is not null && IsResupplyPoint(s, u) && AtResupplyPoint(s, u))
            {
                return; // already there: it refills where it stands
            }
        }
        if (NearestResupplyPoint(u, ResupplySearch) is { } p)
        {
            WalkTo(u, Cell.Of(AimPoint(u.Pos, p)), 0);
        }
    }

    // WalkTo gives ground unit u a move to the n-th free cell around c (its own spot beside a supply point).
    private void WalkTo(Unit u, Cell c, int n)
    {
        if (!Grid.TrySpiral(c, n, out var cell))
        {
            return;
        }
        u.ClearPath();
        u.Order = new Order(OrderKind.Move, cell, 0, 0);
        RequestRoute(u, cell);
    }

    // ResupplyCommand runs CommandKind.Rearm for soldiers (the Resupply order): each listed soldier of the player with carried ammo
    // walks to supply point c.Target (its side's), or with none named the nearest with stock, takes its own spot within its reach,
    // refills there (RunInfantryResupply) and stays. Aircraft take the same command to rearm at an airfield (RearmCommand).
    private void ResupplyCommand(Command c)
    {
        var n = 0;
        foreach (var id in c.Units)
        {
            if (Unit(id) is not { Alive: true, Inside: 0, Garrison: 0, Arms: not null } u || !Commandable(u, c.Player) || u.Def.Speed.Raw == 0 ||
                u.Def.Layer != Layers.Ground || !HasCarried(u))
            {
                continue;
            }
            var p = Unit(c.Target) is { } t && IsResupplyPoint(t, u) ? t : NearestResupplyPoint(u, Fixed.Zero);
            if (p is null)
            {
                continue;
            }
            u.ResetAi();
            WalkTo(u, Cell.Of(AimPoint(u.Pos, p)), n++);
        }
    }

    // HasCarried: u has a weapon with carried ammo.
    private static bool HasCarried(Unit u)
    {
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Effect.Carried > 0 && weapons[i].Stock >= 0)
            {
                return true;
            }
        }
        return false;
    }

    // RunInfantryResupply (the supply system, every 10 ticks): every soldier within 2 cells of a supply point of its side takes
    // one magazine (or one rocket: a launcher's magazine of 1; a weapon without a magazine takes one shot) for its first weapon
    // in weapon order that is not full, paid from the first such point by ID that has the cost in stock (or unlimited stock): a
    // full load costs the weapon's LoadCost, pro rata (floor of the share after minus the share before, so empty to full costs
    // exactly LoadCost). A point that is dry stops refilling.
    private void RunInfantryResupply()
    {
        if (Tick % ResupplyEvery != 0)
        {
            return;
        }
        foreach (var u in _units)
        {
            if (u.Arms is null || !u.Alive || u.Inside != 0 || u.Garrison != 0 || !NeedsResupply(u))
            {
                continue;
            }
            var weapons = u.Def.Weapons;
            for (var i = 0; i < weapons.Count; i++)
            {
                if (weapons[i] is not { Stock: >= 0, Effect.Carried: > 0 } w || u.Charges[w.Stock] >= u.StockMax(w.Stock))
                {
                    continue;
                }
                var max = u.StockMax(w.Stock);
                var before = u.Charges[w.Stock];
                var after = Math.Min(max, before + Math.Max(1, w.Effect.Magazine));
                var cost = (int)((long)w.Effect.LoadCost * after / max - (long)w.Effect.LoadCost * before / max);
                Unit? from = null;
                foreach (var s in _supplySources)
                {
                    if (IsResupplyPoint(s, u) && AtResupplyPoint(s, u) && (s.Def.Supply!.Stock == 0 || s.Stock >= cost))
                    {
                        from = s;
                        break;
                    }
                }
                if (from is null)
                {
                    break; // no point in reach with the stock: nothing this time
                }
                u.MutableCharges[w.Stock] = after;
                if (from.Def.Supply!.Stock > 0)
                {
                    from.Stock -= cost;
                }
                if (u.Arms is { } a && w.Effect.Magazine > 0 && a.Mag[w.Index] == 0 && a.Reloading != w.Index && a.InHand != w.Index)
                {
                    a.MutableMag[w.Index] = Math.Min(w.Effect.Magazine, after); // a weapon not in hand comes back loaded
                }
                break; // one magazine or rocket a time
            }
        }
    }
}
