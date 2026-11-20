using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>What a <see cref="DemolitionEvent"/> reports (INF-EX).</summary>
public enum DemolitionEventKind : byte
{
    /// <summary>A sapper rigged a building (<see cref="DemolitionEvent.Host"/>): his trap is in it.</summary>
    Rigged,

    /// <summary>A building trap went off: <see cref="DemolitionEvent.By"/> went in (garrison, squad or shelter).</summary>
    Triggered,

    /// <summary>A sapper placed a demolition charge on <see cref="DemolitionEvent.Host"/> (<see cref="DemolitionEvent.Timer"/>: on its timer).</summary>
    Placed,

    /// <summary>A demolition charge went off (its sapper's Detonate or its timer).</summary>
    Detonated,

    /// <summary>An enemy sapper (<see cref="DemolitionEvent.By"/>) disarmed a building trap or a demolition charge.</summary>
    Disarmed,

    /// <summary>A sapper of its own side (<see cref="DemolitionEvent.By"/>) lifted a building trap; his kit has the charge back.</summary>
    Lifted,
}

/// <summary>
/// One sapper's explosive event this tick (<see cref="World.DemolitionEvents"/>), for the client: the mark on a rigged building,
/// the blasts, the charge's mark and its removal.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Charge">The building trap or demolition charge (gone after Triggered, Detonated, Disarmed and Lifted).</param>
/// <param name="Host">The building, bridge or vehicle it sits on (0: gone before it went off).</param>
/// <param name="By">The sapper who rigged, placed, disarmed or lifted it, or the unit that went in and set it off; 0 for a
/// charge's blast.</param>
/// <param name="Owner">The side whose trap or charge it is.</param>
/// <param name="Pos">Where it is: the building's centre for a trap, the charge's point on a wall or vehicle.</param>
/// <param name="Timer">Placed: on its timer (it goes off by itself).</param>
public readonly record struct DemolitionEvent(DemolitionEventKind Kind, uint Charge, uint Host, uint By, byte Owner, Vec Pos, bool Timer);

// The demolition system (INF-EX; docs/design.md, "The sapper's building trap and demolition charge"; owner decisions 33 and
// 34): the sapper's two explosives. Both are hidden entities placed by a kit (a placer whose archetype has a demolition block),
// sitting on what they blow (Unit.Host).
//  - The building trap: UseAbility building_trap_kit on a building he could garrison. He walks in at a door, rigs it for the
//    kit's placeTicks and comes out; the trap is in it (Unit.Rig). The first unit of any side to go in (a garrison, a squad's
//    Hold or Ambush, a vehicle into a shelter bay) sets it off (SpringRig). His own side knows (RigKnown): its automatic
//    entries skip the building, a direct order goes in. The same order on a building with his side's trap lifts it from the
//    door, on one with a revealed enemy trap disarms it.
//  - The demolition charge: UseAbility demo_charge_kit on a building (beside its wall), a bridge or a vehicle standing still;
//    Order.Surveil (the command's On) sets its timer. Detonate on the sapper (alive, in radio range) or the timer sets it off
//    (Blow). An enemy ground unit within its seen reach reveals it; the same order on a revealed enemy charge disarms it.
public sealed partial class World
{
    private readonly List<DemolitionEvent> _demolitionEvents = [];
    private readonly List<(UnitDef Def, Vec Pos, Unit Sapper, Unit Host, bool Timer)> _demolitionSpawns = []; // placed this tick
    private TagSet _vehicleTags;
    private bool _vehicleTagsSet;

    /// <summary>The sappers' explosive events this tick (INF-EX), in the order they happened.</summary>
    public IReadOnlyList<DemolitionEvent> DemolitionEvents => _demolitionEvents;

    private enum DemolitionWork : byte
    {
        None,
        Rig,
        Lift,
        Disarm,
        Place,
    }

    /// <summary>The live building trap in building b (<see cref="Unit.Rig"/>), or null.</summary>
    public Unit? RigOn(Unit b) => b.Rig != 0 && Unit(b.Rig) is { Alive: true } t ? t : null;

    /// <summary>
    /// Whether side knows building b is rigged: its own trap, or an enemy's its side revealed (a detector). Its automatic
    /// entries skip such a building; the client's warning mark and the direct order's warning cursor ("Trapped: it will blow").
    /// </summary>
    public bool RigKnown(Unit b, byte side) =>
        RigOn(b) is { } t && (t.Owner == side || (t.RevealedTo & Bit(side)) != 0);

    // The sapper's demolition kit behind ability (his placer whose archetype carries a demolition block), or null.
    private DemolitionEffect? DemolitionKit(Unit u, int ability) =>
        u.Def.Ability(ability) is { Effect: PlacerEffect } ab && Player(u.Owner)?.Setup.Defaults[ab.Archetype] is { Demolition: { } d } ? d : null;

    private bool IsVehicle(Unit t)
    {
        if (!_vehicleTagsSet)
        {
            (_vehicleTags, _vehicleTagsSet) = (Content.TagsOf("vehicle"), true);
        }
        return !t.Def.IsStructure && t.Def.Layer == Layers.Ground && t.Def.Tags.Matches(_vehicleTags);
    }

    // A building a sapper may rig: garrisonable, not an owned post, standing.
    private static bool Riggable(Unit b) =>
        b.Def.Archetype.Garrison is { Owned: false } && b.Alive && b.IsBuilt && !b.Rubble;

    // What a demolition charge may go on: the map's buildings (garrisonable or a neutral civilian structure), a bridge whose
    // span stands, or a vehicle on the ground standing still (any side's).
    private bool ChargeHost(Unit t)
    {
        if (!t.Alive || t.Inside != 0 || t.Garrison != 0 || t.Def.Demolition is not null || t.Def.Trigger is not null)
        {
            return false;
        }
        var a = t.Def.Archetype;
        if (t.Def.IsStructure)
        {
            return t.IsBuilt && !t.Rubble && !a.Invulnerable && !a.Hidden &&
                (a.Bridge is not null ? t.Bridge is { Down: false } : a.Garrison is not null || a.Neutral);
        }
        return IsVehicle(t) && t.Def.Archetype.Flight is null && t.Vel == default;
    }

    // WorkOf resolves what sapper u's demolition order means now, from its target.
    private DemolitionWork WorkOf(Unit u, DemolitionEffect kit, Unit t)
    {
        if (kit.Kind == DemolitionKind.Rig)
        {
            if (!Riggable(t))
            {
                return DemolitionWork.None;
            }
            if (RigOn(t) is { } r && (r.Owner == u.Owner || (r.RevealedTo & Bit(u.Owner)) != 0))
            {
                return r.Owner == u.Owner ? DemolitionWork.Lift : DemolitionWork.Disarm;
            }
            return t.Owner == 0 || t.Owner == u.Owner ? DemolitionWork.Rig : DemolitionWork.None;
        }
        if (t.Def.Demolition is { Kind: DemolitionKind.Charge })
        {
            return t.Alive && t.Owner != u.Owner && (t.RevealedTo & Bit(u.Owner)) != 0 ? DemolitionWork.Disarm : DemolitionWork.None;
        }
        return ChargeHost(t) ? DemolitionWork.Place : DemolitionWork.None;
    }

    // The point a sapper works from for target t: a building's nearest door (rig, lift, disarm a trap), the nearest point of a
    // structure's walls (a charge), else the unit itself; and how close he must be.
    private (Vec At, Fixed Reach) WorkPoint(Unit u, DemolitionEffect kit, Unit t, Fixed range)
    {
        if (kit.Kind == DemolitionKind.Rig)
        {
            return (DoorCell(t, NearestDoor(t, u.Pos)).Center(), t.Def.Archetype.Garrison!.EnterRange);
        }
        return (t.Def.IsStructure ? WallPoint(t, u.Pos) : t.Pos, range);
    }

    // WallPoint returns the point of structure b's footprint nearest p (p itself when inside it).
    private static Vec WallPoint(Unit b, Vec p)
    {
        var o = FootprintOrigin(b);
        var x = Math.Clamp(p.X.Raw, Fixed.FromInt(o.X).Raw, Fixed.FromInt(o.X + FootW(b)).Raw);
        var y = Math.Clamp(p.Y.Raw, Fixed.FromInt(o.Y).Raw, Fixed.FromInt(o.Y + FootH(b)).Raw);
        return new Vec(Fixed.FromRaw(x), Fixed.FromRaw(y));
    }

    // DemolitionCommand takes a UseAbility command for a sapper's demolition kit (true; false: not such a kit). Units whose
    // order means nothing for the target are skipped.
    private bool DemolitionCommand(Command c, int component)
    {
        var any = false;
        foreach (var id in c.Units)
        {
            var u = Unit(id);
            if (u is null || u.Def.Ability(component) is not { } ab || DemolitionKit(u, component) is not { } kit)
            {
                continue;
            }
            any = true;
            if (!Commandable(u, c.Player) || Unit(c.Target) is not { } t || (t.Owner != c.Player && t.Def.Demolition is null && !IsVisibleTo(t, c.Player)))
            {
                continue;
            }
            var work = WorkOf(u, kit, t);
            if (work == DemolitionWork.None || (work is DemolitionWork.Rig or DemolitionWork.Place && u.Charges[ab.Stock] == 0))
            {
                continue;
            }
            Assign(u, new Order(OrderKind.UseAbility, default, t.Id, component, c.On && work == DemolitionWork.Place));
        }
        return any;
    }

    // DemolitionApproach walks a sapper on a demolition order to where he works (true; false: not such an order).
    private bool DemolitionApproach(Unit u)
    {
        if (DemolitionKit(u, u.Order.Ability) is not { } kit)
        {
            return false;
        }
        if (Unit(u.Order.Target) is not { } t || u.Def.Ability(u.Order.Ability) is not { } ab || WorkOf(u, kit, t) == DemolitionWork.None)
        {
            u.Idle();
            return true;
        }
        if (u.Garrison != 0)
        {
            u.ClearPath(); // rigging inside
            return true;
        }
        var (at, reach) = WorkPoint(u, kit, t, ab.Range);
        Approach(u, at, reach);
        return true;
    }

    // RunDemolitions (after the abilities): the sappers' work, then every trap and charge (gone with its host, riding on its
    // vehicle, seen by enemies close by, its timer), in ID order.
    private void RunDemolitions()
    {
        foreach (var u in _units)
        {
            if (u.Order.Kind == OrderKind.UseAbility && u.Alive && u.CanAct && DemolitionKit(u, u.Order.Ability) is { } kit)
            {
                DemolitionStep(u, kit);
            }
        }
        foreach (var c in _units)
        {
            if (c.Def.Demolition is not { } d || !c.Alive)
            {
                continue;
            }
            var host = Unit(c.Host);
            if (host is null || !host.Alive || host.Rubble || host.Bridge is { Down: true })
            {
                c.HP = 0; // gone with what it sat on
                if (host is not null && host.Rig == c.Id)
                {
                    host.Rig = 0;
                }
                continue;
            }
            if (d.Kind == DemolitionKind.Rig)
            {
                DetectRig(c, host);
                continue;
            }
            if (!host.Def.IsStructure && host.Inside == 0)
            {
                c.Pos = host.Pos; // it rides on its vehicle
            }
            if (d.Seen.Raw > 0)
            {
                foreach (var o in _units)
                {
                    if (o.Alive && o.Owner != 0 && o.Owner != c.Owner && o.Inside == 0 && o.Def.Layer == Layers.Ground && !o.Def.IsStructure &&
                        o.Def.Demolition is null && o.Def.Trigger is null && Within(c.Pos, o.Pos, d.Seen))
                    {
                        c.RevealedTo |= Bit(o.Owner);
                    }
                }
            }
            if (c.Fuse > 0 && --c.Fuse == 0)
            {
                Blow(c, d, host);
            }
        }
        foreach (var (def, pos, sapper, host, timer) in _demolitionSpawns)
        {
            var n = Add(def, pos, 0);
            (n.Source, n.Host) = (sapper.Id, host.Id);
            if (def.Demolition!.Kind == DemolitionKind.Rig)
            {
                host.Rig = n.Id;
            }
            n.Fuse = timer ? def.Demolition.TimerTicks : 0;
            _demolitionEvents.Add(new DemolitionEvent(def.Demolition.Kind == DemolitionKind.Rig ? DemolitionEventKind.Rigged : DemolitionEventKind.Placed,
                n.Id, host.Id, sapper.Id, sapper.Owner, pos, timer));
        }
        _demolitionSpawns.Clear();
    }

    // DetectRig: the trap detectors (Archetype detect: the sniper, the observation post, the scout drone) reveal building trap
    // c to their side within their detect reach of its building's walls (the nearest point of its footprint), with fog only
    // while their side sees the building, as for any hidden unit (VisionSystem.cs, RunDetection).
    private void DetectRig(Unit c, Unit b)
    {
        var fog = Fog;
        foreach (var d in _units)
        {
            if (d.Inside != 0 || d.Def.Detect.Raw == 0 || !d.IsBuilt || !d.Alive || d.Owner == c.Owner || d.Owner == 0)
            {
                continue;
            }
            var bit = Bit(d.Owner);
            if ((c.RevealedTo & bit) == 0 && (!fog || (b.SpottedBy & bit) != 0) && Within(d.Pos, AimPoint(d.Pos, b), d.Def.Detect))
            {
                c.RevealedTo |= bit;
            }
        }
    }

    // DemolitionStep: sapper u at work on his demolition order this tick.
    private void DemolitionStep(Unit u, DemolitionEffect kit)
    {
        var ab = u.Def.Ability(u.Order.Ability)!;
        var p = (PlacerEffect)ab.Effect;
        var t = Unit(u.Order.Target);
        var work = t is null ? DemolitionWork.None : WorkOf(u, kit, t);
        if (t is null || work == DemolitionWork.None || (work is DemolitionWork.Rig or DemolitionWork.Place && u.Charges[ab.Stock] == 0))
        {
            u.PlaceLeft = 0;
            u.Idle();
            return;
        }
        var trap = Player(u.Owner)!.Setup.Defaults[ab.Archetype]!;
        if (work == DemolitionWork.Rig)
        {
            if (u.Garrison == t.Id)
            {
                if (u.PlaceLeft == 0)
                {
                    u.PlaceLeft = p.PlaceTicks;
                }
                else if (--u.PlaceLeft == 0)
                {
                    RetireRigs(u, ab.Archetype, p.MaxActive);
                    _demolitionSpawns.Add((trap, t.Pos, u, t, false));
                    u.MutableCharges[ab.Stock]--;
                    LeaveBuilding(u, t, NearestDoor(t, t.Pos), 0); // and he walks out
                }
                return;
            }
            if (u.Garrison != 0)
            {
                u.Idle(); // in another building
                return;
            }
            var door = DoorCell(t, NearestDoor(t, u.Pos)).Center();
            if (!Within(u.Pos, door, t.Def.Archetype.Garrison!.EnterRange))
            {
                return;
            }
            if (!CanGarrison(u, t) || (t.ShelterHolder != 0 && t.ShelterHolder != u.Owner))
            {
                u.Idle(); // full, or held by the other side
                return;
            }
            var order = u.Order;
            EnterBuilding(u, t);
            SpringRig(t, u); // an enemy trap he didn't know of
            if (u.Alive && u.Garrison == t.Id)
            {
                u.Order = order;
                u.PlaceLeft = p.PlaceTicks;
            }
            return;
        }
        var (at, reach) = WorkPoint(u, kit, t, ab.Range);
        if (u.Garrison != 0 || !Within(u.Pos, at, reach))
        {
            u.PlaceLeft = 0;
            return;
        }
        if (u.PlaceLeft == 0)
        {
            // The charge or trap he clears: the target itself (a charge) or the trap in the building.
            var cleared = (t.Def.Demolition is not null ? t : RigOn(t))?.Def.Demolition;
            u.PlaceLeft = work switch
            {
                DemolitionWork.Place => p.PlaceTicks,
                DemolitionWork.Lift => cleared!.LiftTicks,
                _ => cleared!.DisarmTicks,
            };
            u.ClearPath();
            return;
        }
        if (--u.PlaceLeft > 0)
        {
            return;
        }
        if (work == DemolitionWork.Place)
        {
            RetireOldest(u, ab.Archetype, p.MaxActive);
            _demolitionSpawns.Add((trap, at, u, t, u.Order.Surveil));
            u.MutableCharges[ab.Stock]--;
        }
        else
        {
            var gone = t.Def.Demolition is not null ? t : RigOn(t)!;
            var host = Unit(gone.Host);
            gone.HP = 0;
            if (host is not null && host.Rig == gone.Id)
            {
                host.Rig = 0;
            }
            if (work == DemolitionWork.Lift && u.Def.Ability(u.Order.Ability) is { } own)
            {
                u.MutableCharges[own.Stock] = Math.Min(u.Charges[own.Stock] + 1, p.Charges);
            }
            _demolitionEvents.Add(new DemolitionEvent(work == DemolitionWork.Lift ? DemolitionEventKind.Lifted : DemolitionEventKind.Disarmed,
                gone.Id, gone.Host, u.Id, gone.Owner, gone.Pos, false));
        }
        u.Idle();
    }

    // RetireRigs lifts sapper u's oldest building traps until he has room for one more under max (their buildings clear).
    private void RetireRigs(Unit u, int archetype, int max)
    {
        var n = 0;
        foreach (var o in _units)
        {
            if (o.Alive && o.Source == u.Id && o.Def.Archetype.Index == archetype)
            {
                n++;
            }
        }
        foreach (var o in _units)
        {
            if (n < max)
            {
                return;
            }
            if (o.Alive && o.Source == u.Id && o.Def.Archetype.Index == archetype)
            {
                o.HP = 0;
                if (Unit(o.Host) is { } h && h.Rig == o.Id)
                {
                    h.Rig = 0;
                }
                n--;
            }
        }
    }

    // SpringRig sets off building b's trap, if any, as unit by goes in (a garrison, a squad's position, a shelter bay): the
    // building loses its share of max HP, everyone inside takes the inside damage, everyone outside within reach of its walls
    // the outside damage falling to 0 at the reach.
    private void SpringRig(Unit b, Unit by)
    {
        if (RigOn(b) is not { } trap)
        {
            return;
        }
        var d = trap.Def.Demolition!;
        trap.HP = 0;
        trap.Detonated = true;
        b.Rig = 0;
        _hurtBy = null;
        foreach (var o in _units)
        {
            if (o.Alive && (o.Garrison == b.Id || IsShelteredIn(o, b)) && d.Warhead.CanHit(ArmorOf(o)))
            {
                Hurt(o, d.Warhead, d.Inside, 100);
            }
        }
        b.HP -= (int)((long)b.Def.MaxHp * d.BuildingPercent / 100);
        var o0 = FootprintOrigin(b);
        long x0 = Fixed.FromInt(o0.X).Raw, y0 = Fixed.FromInt(o0.Y).Raw;
        long x1 = Fixed.FromInt(o0.X + FootW(b)).Raw, y1 = Fixed.FromInt(o0.Y + FootH(b)).Raw;
        var r = d.Reach.Raw;
        foreach (var o in Around(x0 - r, y0 - r, x1 + r, y1 + r, _nearBlast, true))
        {
            if (!o.Alive || o.Inside != 0 || o.Garrison != 0 || o.Def.IsStructure || o.Def.Demolition is not null || o.Def.Trigger is not null ||
                (o.Def.Archetype.Flight is not null && !Grounded(o)) || !d.Warhead.CanHit(ArmorOf(o)))
            {
                continue;
            }
            var w = new Vec(Fixed.FromRaw(Math.Clamp(o.Pos.X.Raw, x0, x1)), Fixed.FromRaw(Math.Clamp(o.Pos.Y.Raw, y0, y1)));
            var dist = (o.Pos - w).Len().Raw;
            if (dist <= r)
            {
                Hurt(o, d.Warhead, d.Outside, 100 - 100 * dist / r);
            }
        }
        _demolitionEvents.Add(new DemolitionEvent(DemolitionEventKind.Triggered, trap.Id, b.Id, by.Id, trap.Owner, b.Pos, false));
    }

    // DemolitionDetonate takes unit u of a Detonate command by player (true; false: neither a sapper with a demolition kit
    // nor one of his charges): a sapper sets off every charge of his within radio range, a charge goes off if its sapper is
    // alive and within radio range. Each goes off in this tick's demolition pass.
    private bool DemolitionDetonate(Unit u, byte player)
    {
        if (u.Def.Demolition is { Kind: DemolitionKind.Charge } own)
        {
            if (u.Owner == player && u.Alive && Unit(u.Source) is { Alive: true } s && s.Inside == 0 && Within(s.Pos, u.Pos, own.Radio))
            {
                u.Fuse = 1;
            }
            return true;
        }
        var sapper = false;
        foreach (var ab in u.Def.Abilities)
        {
            sapper |= DemolitionKit(u, ab.Component) is { Kind: DemolitionKind.Charge };
        }
        if (!sapper)
        {
            return false;
        }
        if (u.Owner != player || !u.Alive || u.Inside != 0)
        {
            return true;
        }
        foreach (var c in _units)
        {
            if (c.Alive && c.Source == u.Id && c.Def.Demolition is { Kind: DemolitionKind.Charge } d && Within(u.Pos, c.Pos, d.Radio))
            {
                c.Fuse = 1;
            }
        }
        return true;
    }

    // Blow sets off demolition charge c on host: a building up to the large size falls, a larger one loses its share of max HP
    // a charge (a garrisonable one left standing gets a breach, a new door, where the charge was); a bridge's span falls; a
    // vehicle takes the vehicle damage; then the blast round the charge.
    private void Blow(Unit c, DemolitionEffect d, Unit host)
    {
        c.HP = 0;
        c.Detonated = true;
        _hurtBy = null;
        if (!host.Def.IsStructure && host.Inside == 0)
        {
            c.Pos = host.Pos; // on its vehicle, where it is now
        }
        if (host.Def.Archetype.Bridge is not null)
        {
            host.HP = 0; // the span falls (BridgeSystem.cs), until repaired
        }
        else if (host.Def.IsStructure)
        {
            if (host.Def.MaxHp > d.LargeHp)
            {
                host.HP -= (int)((long)host.Def.MaxHp * d.BuildingPercent / 100);
                if (host.HP > 0 && host.Def.Archetype.Garrison is not null && !host.Breached)
                {
                    (host.Breached, host.BreachCell) = (true, BreachAt(host, c.Pos));
                }
            }
            else
            {
                host.HP = 0;
            }
        }
        else if (d.Warhead.CanHit(ArmorOf(host)))
        {
            Hurt(host, d.Warhead, d.Vehicle, 100);
        }
        if (d.Blast > 0)
        {
            Explode(c.Pos, d.Blast, d.Warhead, d.Reach, except: host.Id, center: 100, edge: 0);
        }
        _demolitionEvents.Add(new DemolitionEvent(DemolitionEventKind.Detonated, c.Id, host.Id, 0, c.Owner, c.Pos, false));
    }

    // BreachAt returns the cell just outside building b's walls at point p on them.
    private static Cell BreachAt(Unit b, Vec p)
    {
        var o = FootprintOrigin(b);
        int w = FootW(b), h = FootH(b);
        var c = Cell.Of(p);
        if (c.X >= o.X && c.X < o.X + w && c.Y >= o.Y && c.Y < o.Y + h)
        {
            // On the west or north wall (a point on the east or south wall floors to the cell outside already).
            c = p.X.Raw == Fixed.FromInt(o.X).Raw ? new Cell(o.X - 1, c.Y) : new Cell(c.X, o.Y - 1);
        }
        return c;
    }
}
