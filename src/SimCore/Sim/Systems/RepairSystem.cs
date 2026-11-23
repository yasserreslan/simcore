using System.Runtime.CompilerServices;
using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Sim;

/// <summary>What a repair event reports (<see cref="World.RepairEvents"/>).</summary>
public enum RepairEventKind : byte
{
    /// <summary>An engineer started repairing a vehicle (in reach, his blowtorch lit).</summary>
    Started,

    /// <summary>He stopped: the vehicle is full, gone, out of reach, or he was ordered away.</summary>
    Stopped,
}

/// <summary>An engineer's repair starting or stopping this tick, for the client's blowtorch flame and sparks.</summary>
/// <param name="Kind">Started or stopped.</param>
/// <param name="Engineer">The engineer.</param>
/// <param name="Target">The vehicle.</param>
public readonly record struct RepairEvent(RepairEventKind Kind, uint Engineer, uint Target);

// The repair system (INF-E1a, the owner 2026-11-20: "he can click on a vehicle and can go repair it with a repair tool,
// blowtorch"): a unit with a repair kit (every Resistance engineer carries one) repairs one friendly vehicle at a time, the
// one the player names with a Repair order (CommandKind.Repair on it), or with no order of the player's, the nearest damaged
// friendly vehicle within the kit's radius (3 cells; checked once a second). He walks to within 1.5 cells of it (a cell off its
// hull), stands and adds the kit's rate every EveryTicks until it is full, gone, or he is ordered away; several engineers stack.
// Started and Stopped events tell the client. Today's passive aura (every vehicle within 2.5 cells) is gone (decision 1118).
// Units in ID order; Unit.RepairTarget, RepairWalk and Repairing are hashed.
public sealed partial class World
{
    private static readonly Fixed RepairReach = Fixed.Frac(15, 10); // within 1.5 cells of the vehicle's centre: a cell off its hull
    private const int RepairScanEvery = 20; // an idle engineer looks for a damaged vehicle once a second

    private readonly List<RepairEvent> _repairEvents = [];

    /// <summary>This tick's repairs started and stopped.</summary>
    public IReadOnlyList<RepairEvent> RepairEvents => _repairEvents;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // BATTLE-PERF: run every tick, compiled optimised at once (row 1390)
    private void RunRepair()
    {
        foreach (var r in _units)
        {
            if (r.Def.Repairs.Count == 0 || !r.Alive)
            {
                continue;
            }
            var e = r.Def.Repairs[0];
            if (r.RepairTarget != 0 && (!r.CanAct || r.Inside != 0 || OrderedAway(r) || Unit(r.RepairTarget) is not { } t0 || !Repairable(r, t0, e)))
            {
                StopRepair(r);
            }
            if (r.RepairTarget == 0 && r.CanAct && r.Inside == 0 && r.Garrison == 0 && r.Order.Kind == OrderKind.Idle &&
                (Tick + r.Id) % RepairScanEvery == 0 && NearestRepairable(r, e) is { } near)
            {
                r.RepairTarget = near.Id; // on his own: a damaged vehicle beside him
            }
            if (r.RepairTarget == 0 || Unit(r.RepairTarget) is not { } t)
            {
                continue;
            }
            if (!Within(r.Pos, t.Pos, RepairReach))
            {
                if (r.Repairing)
                {
                    (r.Repairing, r.RepairWalk) = (false, default);
                    _repairEvents.Add(new RepairEvent(RepairEventKind.Stopped, r.Id, t.Id)); // it drove off: he follows
                }
                if (r.Order.Kind == OrderKind.Idle || (Tick + r.Id) % RepairScanEvery == 0)
                {
                    WalkTo(r, Cell.Of(t.Pos), 0);
                    r.RepairWalk = r.Order.Kind == OrderKind.Move ? r.Order.Cell : default;
                }
                continue;
            }
            if (!r.Repairing)
            {
                if (r.Order.Kind == OrderKind.Move)
                {
                    r.Idle(); // in reach: he stops and lights the torch
                }
                (r.Repairing, r.RepairWalk) = (true, default);
                _repairEvents.Add(new RepairEvent(RepairEventKind.Started, r.Id, t.Id));
            }
            if (Tick % (uint)e.EveryTicks == 0)
            {
                t.HP = Math.Min(t.Def.MaxHp, t.HP + e.Rate);
            }
        }
    }

    // Repairable: t is a damaged friendly vehicle (the kit's tags) out on the map.
    private static bool Repairable(Unit r, Unit t, RepairEffect e) =>
        t != r && t.Alive && t.Owner == r.Owner && t.Inside == 0 && t.HP < t.Def.MaxHp && t.Def.Tags.Matches(e.Affects);

    // OrderedAway: the player gave r another order than the walk to its repair (or than standing at it).
    private static bool OrderedAway(Unit r) =>
        !(r.Order.Kind == OrderKind.Idle || (r.Order.Kind == OrderKind.Move && r.RepairWalk != default && r.Order.Cell == r.RepairWalk));

    // NearestRepairable is the nearest damaged friendly vehicle within the kit's radius of r (ties to the lower ID), or null.
    private Unit? NearestRepairable(Unit r, RepairEffect e)
    {
        Unit? best = null;
        long bestD = 0;
        foreach (var t in _units)
        {
            if (!Repairable(r, t, e) || !Within(r.Pos, t.Pos, e.Radius))
            {
                continue;
            }
            var d = (t.Pos - r.Pos).LenSq();
            if (best is null || d < bestD)
            {
                (best, bestD) = (t, d);
            }
        }
        return best;
    }

    // StopRepair ends r's repair (an event if it was under way) and its walk there.
    private void StopRepair(Unit r)
    {
        if (r.Repairing)
        {
            _repairEvents.Add(new RepairEvent(RepairEventKind.Stopped, r.Id, r.RepairTarget));
        }
        if (r.Order.Kind == OrderKind.Move && r.RepairWalk != default && r.Order.Cell == r.RepairWalk)
        {
            r.Idle();
        }
        (r.RepairTarget, r.Repairing, r.RepairWalk) = (0, false, default);
    }

    // VehicleRepairCommand runs CommandKind.Repair for units with a repair kit aimed at a damaged friendly vehicle: each walks to
    // it and repairs it until it is full or he is ordered away (structures and runways: RepairCommand).
    private void VehicleRepairCommand(Command c)
    {
        if (Unit(c.Target) is not { } t)
        {
            return;
        }
        foreach (var id in c.Units)
        {
            if (Unit(id) is not { Alive: true, Inside: 0, Garrison: 0 } r || !Commandable(r, c.Player) || r.Def.Repairs.Count == 0 ||
                !Repairable(r, t, r.Def.Repairs[0]))
            {
                continue;
            }
            StopRepair(r);
            r.ResetAi();
            r.RepairTarget = t.Id;
            WalkTo(r, Cell.Of(t.Pos), 0);
            r.RepairWalk = r.Order.Kind == OrderKind.Move ? r.Order.Cell : default;
        }
    }
}
