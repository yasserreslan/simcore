using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

// The order system: each unit updates its order, plans its path toward
// whatever it must reach, and picks a target to shoot.
public sealed partial class World
{
    private const uint RepathTicks = TicksPerSecond; // chasing units re-plan once a second

    private void RunOrders()
    {
        foreach (var u in _units)
        {
            Think(u);
        }
        DropIndex();
    }

    // Think updates a unit's order and target.
    private void Think(Unit u)
    {
        if (u.Inside != 0)
        {
            return;
        }
        if (u.Def.Strike is not null)
        {
            Guide(u);
            return;
        }
        if (u.Def.Archetype.Flight is not null)
        {
            return; // a scout or strike drone runs its own orders (the scout and watcher systems)
        }
        if (u.Garrison != 0)
        {
            Hold(u);
        }
        if (u.Ai != AiState.None && !AiAllowed(u))
        {
            // An order took over, or a stance that never moves on its own: the chase is off.
            if (u.Order.Kind == OrderKind.Idle)
            {
                u.ClearPath();
            }
            u.ResetAi();
        }
        if (u.MoveOn && u.Order.Kind != OrderKind.AttackMove)
        {
            u.MoveOn = false;
        }
        switch (u.Order.Kind)
        {
            case OrderKind.Move:
                // A ground unit has arrived when it has nothing left to walk or wait for.
                var arrived = u.Def.Layer == Layers.Air ? u.Pos == u.Order.Cell.Center()
                    : !u.Moving && !u.PathPending && u.Mode != RouteMode.Field && u.RouteIdx >= u.Route.Length;
                if (arrived)
                {
                    var deploy = u.DeployAfterMove;
                    u.Idle();
                    if (deploy && u.Deploy == DeployState.Stowed && CanDeployAt(u, Cell.Of(u.Pos)))
                    {
                        StartDeploy(u); // a Deploy command that first had to leave a slope
                    }
                }
                break;
            case OrderKind.Attack:
                var target = Unit(u.Order.Target);
                if (target is { Alive: true } && !IsVisibleTo(target, u.Owner) && CanSeek(u))
                {
                    // Lost in the fog: on to where it was last seen, ready to fight (an attack-move).
                    var last = u.Order.Cell;
                    u.ClearPath();
                    u.Order = new Order(OrderKind.AttackMove, last, 0, 0);
                    RequestRoute(u, last);
                    break;
                }
                var w = target is null || !IsVisibleTo(target, u.Owner) ? -1 : PickWeapon(u, target, false);
                if (w < 0)
                {
                    u.Idle(); // gone, hidden again or out of ammo
                    break;
                }
                u.Order = u.Order with { Cell = Cell.Of(target!.Pos) }; // where it was last seen
                if (u.Guiding != 0 || u.AimAt != 0)
                {
                    u.ClearPath(); // aiming or guiding a missile: it stands still
                    break;
                }
                var aimAt = AimPoint(u.Pos, target!);
                Approach(u, aimAt, ReachTo(u, aimAt, u.Def.Weapons[w]), LineOfFire(u, aimAt, u.Def.Weapons[w], target));
                break;
            case OrderKind.Enter:
                var v = Unit(u.Order.Target);
                if (v?.Def.Archetype.Tunnel is not null)
                {
                    if (!CanUseTunnel(u, v))
                    {
                        u.Idle(); // gone, rubble, or too big for it
                        break;
                    }
                    Approach(u, TunnelDoor(v), EnterReach); // it waits at the door while the network is full
                    break;
                }
                if (v?.Def.Archetype.Garrison is { } g)
                {
                    if (!CanGarrison(u, v))
                    {
                        u.Idle(); // full, taken by the enemy, or gone
                        break;
                    }
                    Approach(u, DoorCell(v, NearestDoor(v, u.Pos)).Center(), g.EnterRange);
                    break;
                }
                if (v?.Def.Archetype.Airlift is { } al)
                {
                    if (!CanAirlift(u, v))
                    {
                        u.Idle(); // gone, full, taken off, or not a vehicle it takes
                        break;
                    }
                    if (u.AirliftBoard == 0)
                    {
                        Approach(u, AirliftBoardSpot(u, v), al.BoardRange); // to its ramp, or the nearest it can stand to it (it stands still going up it)
                    }
                    break;
                }
                if (v?.Heli is not null)
                {
                    if (!CanRideHeli(u, v))
                    {
                        u.Idle(); // full, gone, or not a rider it takes
                        break;
                    }
                    Approach(u, v.Pos, v.Def.Archetype.Flight!.Rotor!.Riders!.BoardRange); // to where it hovers or stands (it lands to take him)
                    break;
                }
                if (v is null || !CanEnter(u, v))
                {
                    u.Idle();
                    break;
                }
                Approach(u, v.Def.IsStructure ? AimPoint(u.Pos, v) : v.Pos, v.Def.Crew!.EnterRange); // a crewed emplacement: from its footprint
                break;
            case OrderKind.UseAbility:
                var ab = u.Def.Ability(u.Order.Ability);
                Unit? t = null;
                if (ab is null || u.Charges[ab.Stock] == 0 ||
                    (u.Order.Target != 0 && ((t = Unit(u.Order.Target)) is null || !IsVisibleTo(t, u.Owner))))
                {
                    u.Idle();
                    break;
                }
                Approach(u, t?.Pos ?? u.Order.Cell.Center(), ab.Range);
                break;
            case OrderKind.AttackGround:
                var gw = GroundWeapon(u, false);
                if (gw < 0)
                {
                    u.Idle(); // out of ammo for every weapon that can hit the ground
                    break;
                }
                if (u.Guiding != 0 || u.AimAt != 0)
                {
                    u.ClearPath(); // aiming or guiding a missile: it stands still
                    break;
                }
                Approach(u, u.Order.Cell.Center(), ReachTo(u, u.Order.Cell.Center(), u.Def.Weapons[gw]),
                    LineOfFire(u, u.Order.Cell.Center(), u.Def.Weapons[gw], null));
                break;
            case OrderKind.Unload:
                var dep = Unit(u.Order.Target);
                if (dep is not null && dep.Def.TruckReloadWeapon >= 0 && u.Def.Cargo is not null)
                {
                    // A gun that trucks reload (the howitzer): into the reload's range of its footprint, a cell short of it; the
                    // handover is the cargo system's (HowitzerSystem.cs), which ends the order.
                    if (!CanFeed(u, dep))
                    {
                        u.Idle(); // gone, not ours, full already, or the truck is short of a round
                        break;
                    }
                    var fr = TruckReloadOf(dep)!.Range;
                    Approach(u, AimPoint(u.Pos, dep), fr.Raw > Fixed.OneRaw ? fr - Fixed.One : fr);
                    break;
                }
                if (dep is null || u.Def.Cargo is null || u.Cargo <= 0 || !IsCargoDepot(dep, u))
                {
                    u.Idle(); // gone, not ours, or the truck is empty
                    break;
                }
                // Into the radius, a cell short of its edge, so it stops inside.
                var r = dep.Def.Supply!.Radius - Fixed.FromInt(1);
                Approach(u, dep.Pos, r.Raw > 0 ? r : dep.Def.Supply.Radius);
                break;
            case OrderKind.LoadCargo:
                var src = Unit(u.Order.Target);
                if (src is null || u.Def.Cargo is not { } lc || u.Cargo >= lc.Amount || !IsCargoSource(src, u))
                {
                    u.Idle(); // gone, not ours, or the truck is full
                    break;
                }
                // Into the store's radius, a cell short of its edge, so it stops inside (logistics).
                var sr = src.Def.Supply!.Radius - Fixed.FromInt(1);
                Approach(u, src.Pos, sr.Raw > 0 ? sr : src.Def.Supply.Radius);
                break;
            case OrderKind.Build:
                var site = Unit(u.Order.Target);
                if (site is null || site.IsBuilt || site.Owner != u.Owner || u.Def.Builder is not { } builder)
                {
                    u.Idle(); // gone, finished or not ours
                    break;
                }
                Approach(u, AimPoint(u.Pos, site), builder.Range);
                break;
            case OrderKind.Repair:
                ThinkRepair(u); // AirfieldDamageSystem.cs
                break;
            case OrderKind.Idle:
                if (u.Ai == AiState.None && u.Stance == Stance.Aggressive && Pursues(u) && (Tick + u.Id) % (uint)Content.UnitAi.ScanTicks == 0 &&
                    !(Unit(u.AutoTarget) is { } held && CanAutoTarget(u, held)) && GuardScan(u, u.Pos, AggressiveRange(u)) is { } prey)
                {
                    Engage(u, prey, prey.Pos); // Aggressive: it goes after what it spots, on the leash
                }
                RunAi(u);
                break;
            case OrderKind.AttackMove:
                AttackMove(u);
                break;
            case OrderKind.Guard:
                if (u.Order.Target != 0)
                {
                    if (Unit(u.Order.Target) is { Alive: true } ward && ward.Owner == u.Owner)
                    {
                        if (ward.Inside == 0)
                        {
                            u.Order = u.Order with { Cell = Cell.Of(ward.Pos) }; // the circle moves with it
                        }
                    }
                    else
                    {
                        u.Order = u.Order with { Target = 0 }; // it fell: guard where it was
                    }
                }
                GuardPost(u);
                break;
        }

        if (u.Order.Kind is OrderKind.Attack or OrderKind.AttackGround || u.Def.Weapons.Count == 0 || !u.CanAct)
        {
            u.AutoTarget = 0;
            return;
        }
        if (u.Ai == AiState.Engage && Unit(u.AiTarget) is { } chased && IsSpotted(chased, u.Owner) && PickWeapon(u, chased, true) >= 0)
        {
            u.AutoTarget = chased.Id; // the chase's target first, once in reach
            return;
        }
        switch (u.Stance)
        {
            case Stance.HoldFire:
                u.AutoTarget = 0; // only orders make it fire
                return;
            case Stance.ReturnFire:
                if (u.LastHitBy != 0 && Unit(u.LastHitBy) is not { Alive: true })
                {
                    u.LastHitBy = 0; // whoever hurt it is gone
                }
                u.AutoTarget = Unit(u.LastHitBy) is { } attacker && CanAutoTarget(u, attacker) ? attacker.Id : 0;
                return;
            case Stance.Guard or Stance.HoldGround or Stance.Aggressive:
                break;
        }
        // Keep the current auto target while it stays valid, so fire doesn't flicker between
        // enemies, unless it is harmless and the enemy that just hurt this unit is in reach.
        if (Unit(u.AutoTarget) is { } cur && CanAutoTarget(u, cur))
        {
            if (Harmless(u, cur) && RecentAttacker(u) is { } attacker && attacker != cur && CanAutoTarget(u, attacker))
            {
                u.AutoTarget = attacker.Id;
            }
            return;
        }
        // Without a target it searches when it has just looked around (RunVision: every VisionTicks
        // ticks on its ID's phase, on entering a cell, when new) or on that phase anyway; a unit
        // whose target just became invalid searches at once.
        if (u.AutoTarget != 0 || u.LookedAt == Tick || (Tick + u.Id) % VisionTicks == 0)
        {
            u.AutoTarget = NearestTarget(u);
        }
    }

    // Approach stops a unit once goal is within r, and otherwise keeps a
    // route toward it, re-planned about once a second as the goal moves
    // (staggered by ID, so re-plans spread over the ticks).
    private void Approach(Unit u, Vec goal, Fixed r, bool clear = true)
    {
        // In reach without a line of fire (a ridge in the way): it keeps closing in until it has one.
        if ((Within(u.Pos, goal, r) && clear) || u.Garrison != 0)
        {
            if (u.Garrison == 0 && u.Def.Archetype.DeployTicks > 0 && u.Deploy == DeployState.Stowed && !CanDeployAt(u, Cell.Of(u.Pos)))
            {
                // In reach, but on ground too steep to set up on: to the nearest spot it can
                // (still in reach), or it stays and doesn't fire.
                if (!u.Moving && !u.PathPending && DeploySpot(u, goal, r) is { } spot)
                {
                    RequestRoute(u, spot);
                }
                return;
            }
            u.ClearPath(); // a soldier in a building never walks
            return;
        }
        if (u.Def.Speed.Raw > 0 && u.CanAct && u.Def.Layer == Layers.Ground && Tick >= u.RepathAt)
        {
            RequestRoute(u, Cell.Of(goal));
            u.RepathAt = Tick + RepathTicks + u.Id % RepathTicks;
        }
    }

    // Hold drops the orders a garrisoned soldier can't carry out from inside:
    // moving, entering, abilities, and attacks on targets out of reach.
    private void Hold(Unit u)
    {
        var keep = u.Order.Kind switch
        {
            OrderKind.Idle => true,
            OrderKind.Attack => Unit(u.Order.Target) is { } t && IsVisibleTo(t, u.Owner) && PickWeapon(u, t, true) >= 0,
            OrderKind.AttackGround => GroundWeapon(u, true) >= 0,
            _ => false,
        };
        if (!keep)
        {
            u.Idle();
        }
    }

    // Guide keeps a launched drone's goal current: it follows its target
    // unit, and flies on to the last cell it saw it in once the target is gone.
    private void Guide(Unit u)
    {
        if (u.Order.Kind != OrderKind.Attack)
        {
            return;
        }
        if (Unit(u.Order.Target) is { Inside: 0 } t)
        {
            u.Order = u.Order with { Cell = Cell.Of(t.Pos) };
        }
        else
        {
            u.Order = u.Order with { Kind = OrderKind.Move, Target = 0 };
        }
    }

    // CanAutoTarget reports whether u may keep shooting t without orders.
    // Launchers that deploy (rocket trucks) never pick targets themselves: they fire on orders only;
    // a unit with AutoFireDeployed (the mortar) does once it is set up.
    // Structures that can't fight (depots, garages, tunnel entrances) are left to orders: only armed
    // ones and buildings an enemy garrison holds are picked (the Zero Hour rule).
    private bool CanAutoTarget(Unit u, Unit t) => CanAutoTarget(u, t, out _);

    // wi: the weapon PickWeapon chose for t (in range), when it may.
    private bool CanAutoTarget(Unit u, Unit t, out int wi)
    {
        wi = -1;
        if (!PicksTargets(u) || t.Owner == u.Owner || t.Owner == 0 || !t.Alive || !IsSpotted(t, u.Owner) || !Fights(t))
        {
            return false;
        }
        wi = PickWeapon(u, t, true);
        if (wi < 0)
        {
            return false;
        }
        var e = u.Def.Weapons[wi].Effect;
        if (e.MaxMisses > 0 && u.MissTarget == t.Id && u.MissStreak >= e.MaxMisses)
        {
            return false; // it kept missing this one: leave it
        }
        if (Restricted(e, t) is { } limit)
        {
            if (!Within(ShotFrom(u, t.Pos), AimPoint(u.Pos, t), limit + u.RangeBonus))
            {
                return false;
            }
            if (e.AutoSkipRetreating && Velocity(t) is { } v && v.X.Raw * (t.Pos.X.Raw - u.Pos.X.Raw) + v.Y.Raw * (t.Pos.Y.Raw - u.Pos.Y.Raw) > 0)
            {
                return false; // moving away
            }
        }
        return true;
    }

    // PicksTargets reports whether u picks targets on its own at all: units that don't deploy, and
    // those with AutoFireDeployed once set up.
    private static bool PicksTargets(Unit u) =>
        u.Def.Archetype.DeployTicks == 0 || (u.Def.Archetype.AutoFireDeployed && u.Deploy == DeployState.Deployed);

    // Restricted returns the auto-acquire distance limit of weapon e against t's armor class, or null.
    private static Fixed? Restricted(WeaponEffect e, Unit t) =>
        t.Def.Armor < e.AutoRange.Count && e.AutoRange[t.Def.Armor].Raw > 0 ? e.AutoRange[t.Def.Armor] : null;

    // Fights reports whether t is worth picking on one's own: anything but a structure, or a
    // structure that is armed or holds soldiers.
    private static bool Fights(Unit t) =>
        !t.Def.IsStructure || t.Def.Weapons.Count > 0 || (t.Def.Archetype.Garrison is not null && t.Occupants > 0);

    // Harmless reports whether t has no weapon that could ever hit u (its layer and armor): workers,
    // trucks, the carrier, traps. A building's occupants count for it.
    private bool Harmless(Unit u, Unit t)
    {
        if (t.Def.Archetype.Garrison is not null && t.Occupants > 0)
        {
            return false;
        }
        var weapons = t.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Effect.Interceptor is null && CanHit(weapons[i], u))
            {
                return false;
            }
        }
        return true;
    }

    // RecentAttacker is the enemy that hurt u within the chase time, if it still lives.
    private Unit? RecentAttacker(Unit u) =>
        u.LastHitBy != 0 && Tick <= u.LastHitTick + (uint)Content.UnitAi.ChaseTicks && Unit(u.LastHitBy) is { Alive: true } a ? a : null;

    // Priority orders u's candidate targets (lower first), for weapon wi: the enemy that just hurt it,
    // then threats (they can hit it) before harmless units, then targets the weapon engages freely
    // before restricted ones (autoRange), then for a weapon with a groupRadius (the howitzer) the
    // biggest group (GroupAround), then the weapon's priority list by armor class. Nearest and
    // the lowest ID break ties (the callers).
    private long Priority(Unit u, Unit t, int wi)
    {
        var w = u.Def.Weapons[wi];
        var e = w.Effect;
        long attacker = t.Id == u.LastHitBy && RecentAttacker(u) is not null ? 0 : 1;
        long harmless = Harmless(u, t) ? 1 : 0;
        long lesser = Restricted(e, t) is not null ? 1 : 0;
        long group = e.GroupRadius.Raw > 0 ? MaxGroup - Math.Min(MaxGroup, GroupAround(u, t, w)) : 0;
        long rank = t.Def.Armor < e.Priority.Count ? e.Priority[t.Def.Armor] : 0;
        return (attacker << 40) | (harmless << 32) | (lesser << 24) | (group << 12) | rank;
    }

    private const long MaxGroup = 4095;
    private readonly List<Unit> _nearGroup = []; // scratch: the units round a candidate target (GroupAround)

    // GroupAround tallies the group round candidate t for u's weapon w (its groupRadius): every enemy of u its side spots that
    // the weapon can hit, out in the open, whose nearest point is within the radius of t's position (t included), a structure
    // counting the weapon's groupStructure, anything else 1. A pure count: the order of the candidates does not matter.
    private int GroupAround(Unit u, Unit t, WeaponDef w)
    {
        var e = w.Effect;
        var m = e.GroupRadius.Raw + Fixed.OneRaw;
        var n = 0;
        foreach (var o in Around(t.Pos.X.Raw - m, t.Pos.Y.Raw - m, t.Pos.X.Raw + m, t.Pos.Y.Raw + m, _nearGroup, true))
        {
            if (o.Owner == u.Owner || o.Owner == 0 || !o.Alive || o.Inside != 0 || o.Garrison != 0 || !CanHit(w, o) || !IsSpotted(o, u.Owner) ||
                !Within(t.Pos, AimPoint(t.Pos, o), e.GroupRadius))
            {
                continue;
            }
            n += o.Def.IsStructure ? e.StructureWeight : 1;
        }
        return n;
    }

    // NearestTarget returns the closest spotted enemy that one of u's
    // weapons can hit from where u stands, ties going to the lowest ID, or 0.
    // Only units within u's longest reach can qualify (CanAutoTarget needs a
    // weapon in reach), so it checks the spatial index's candidates; the tie
    // break on ID makes their order irrelevant.
    private uint NearestTarget(Unit u)
    {
        if (!PicksTargets(u))
        {
            return 0; // launchers that deploy never pick targets (see CanAutoTarget)
        }
        long reach = 0;
        var weapons = u.Def.Weapons;
        for (var i = 0; i < weapons.Count; i++)
        {
            if (weapons[i].Effect.Interceptor is not null)
            {
                continue; // air defence picks its own targets (RunAirDefense)
            }
            reach = Math.Max(reach, Reach(u, weapons[i]).Raw * (Ground is null ? 100 : 100 + RangeHeightCapPercent) / 100);
        }
        if (reach == 0)
        {
            return 0;
        }
        uint best = 0;
        long bestD = 0, bestKey = 0;
        foreach (var o in AroundOthers(u, reach, u.Owner, true, _nearSee)) // CanAutoTarget drops own and neutral units
        {
            if (!CanAutoTarget(u, o, out var wi))
            {
                continue;
            }
            var key = Priority(u, o, wi); // the weapon CanAutoTarget picked: PickWeapon is pure (its line of fire too)
            var d = (o.Pos - u.Pos).LenSq();
            if (best == 0 || key < bestKey || (key == bestKey && (d < bestD || (d == bestD && o.Id < best))))
            {
                best = o.Id;
                bestD = d;
                bestKey = key;
            }
        }
        return best;
    }
}
