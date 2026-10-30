using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Mission-owned tunnel trip: 0 board, 2 transit, 3 evacuate. Underground members retain ownership.</summary>
public sealed record AiTunnelTripMemory(uint Entry, uint Exit, uint Started, uint Until, uint NextOrder, int Stage, bool BoardingCancelled);

public sealed partial class PlayerAi
{
    private sealed class TunnelTrip(uint entry, uint exit, uint tick, uint until)
    {
        public readonly uint Entry = entry, Exit = exit, Started = tick, Until = until;
        public uint NextOrder;
        public int Stage;
        public bool BoardingCancelled;
    }
    private sealed record TunnelRoute(uint Entry, uint Exit, long Score);

    private bool MissionReachable(World w, Mission m, Cell point) =>
        m.Members.All(id => Own(id) is { } u && Reachable(w, u, point, out _)) || FindTunnelRoute(w, m, point, false) is not null;

    private bool ReachableFrom(World w, Unit unit, Cell from, Cell to)
    {
        var r = w.Routing;
        var k = r.ClassOf(unit.Def);
        if (k < 0) { return false; }
        var a = r.NearestPassable(k, from, _rules.ReachSearchCells);
        var b = r.NearestPassable(k, to, _rules.ReachSearchCells);
        return a >= 0 && b >= 0 && r.Component(k, a) != 0 && r.Component(k, a) == r.Component(k, b);
    }

    private TunnelRoute? FindTunnelRoute(World w, Mission m, Cell point, bool requireSavings)
    {
        if (Doctrine?.Tunnels is not { } p || m.Members.Count == 0 ||
            !m.Members.All(id => Own(id) is { } u && Mobile(u) && u.Def.Archetype.TunnelSize > 0)) { return null; }
        var units = m.Members.Select(id => Own(id)!).ToArray();
        var nodes = w.TunnelNodes(Player).Take(p.MaxNodes).ToArray();
        var size = units.Max(u => u.Def.Archetype.TunnelSize);
        var speed = Math.Max(1, units.Min(u => u.Def.Speed.Raw));
        var lead = units[0];
        var slots = units.Sum(w.TunnelSlotsOf);
        var strength = units.Sum(u => (long)Strength(u));
        var entries = nodes.Where(n => !n.Rubble && n.Def.Archetype.Tunnel!.Size >= size &&
                units.All(u => World.CanUseTunnel(u, n) && Near(Cell.Of(u.Pos), Cell.Of(World.TunnelDoor(n)), p.MaxEntryCells) &&
                    Reachable(w, u, Cell.Of(World.TunnelDoor(n)), out _)))
            .OrderBy(n => Distance(Cell.Of(lead.Pos), Cell.Of(n.Pos))).ThenBy(n => n.Id).Take(p.Candidates).ToArray();
        var exits = nodes.Where(n => !n.Rubble && n.Def.Archetype.Tunnel!.Size >= size &&
                units.All(u => ReachableFrom(w, u, Cell.Of(World.TunnelDoor(n)), point)))
            .OrderBy(n => Distance(Cell.Of(n.Pos), point)).ThenBy(n => n.Id).Take(p.Candidates).ToArray();
        TunnelRoute? best = null;
        var checks = 0;
        foreach (var entry in entries)
        {
            var network = w.TunnelNetwork(entry);
            var load = w.TunnelLoad(entry);
            var reserved = _missions.Where(other => other != m && !other.Done && other.Tunnel is { } trip && network.Any(n => n.Id == trip.Entry))
                .Sum(other => other.Members.Where(id => Own(id) is { InTunnel: false }).Sum(id => w.TunnelSlotsOf(Own(id)!)));
            if (load.Used + reserved + slots > load.Capacity) { continue; }
            foreach (var exit in exits)
            {
                if (entry == exit) { continue; }
                if (++checks > p.MaxPairs) { return best; }
                var danger = Danger(w, Cell.Of(World.TunnelDoor(exit)), false);
                if (danger * 100 > Math.Max(1, strength) * p.DangerPercent) { continue; }
                var underground = TunnelTicks(nodes, entry.Id, exit.Id, size, false);
                if (underground == long.MaxValue) { continue; }
                var approach = units.Max(u => Fixed.Sqrt(Distance(Cell.Of(u.Pos), Cell.Of(World.TunnelDoor(entry))))) / speed;
                var onward = Fixed.Sqrt(Distance(Cell.Of(World.TunnelDoor(exit)), point)) / speed;
                var throughput = (long)units.Length * (entry.Def.Archetype.Tunnel!.Throughput + exit.Def.Archetype.Tunnel!.Throughput);
                var eta = approach + underground + onward + throughput + entry.Gate + exit.Gate;
                var direct = Fixed.Sqrt(Distance(Cell.Of(lead.Pos), point)) / speed;
                var surface = units.All(u => Reachable(w, u, point, out _));
                if (requireSavings && surface && direct - eta < p.MinSavingTicks) { continue; }
                var score = eta + danger * _rules.Foundation!.DangerWeight;
                if (best is null || score < best.Score || score == best.Score && (entry.Id < best.Entry || entry.Id == best.Entry && exit.Id < best.Exit))
                { best = new TunnelRoute(entry.Id, exit.Id, score); }
            }
        }
        return best;
    }

    // Route only across the friendly node snapshot. Size-3 vehicles may pass medium junctions, but cannot emerge there.
    private static long TunnelTicks(IReadOnlyList<Unit> nodes, uint from, uint to, int size, bool rubble)
    {
        var start = -1; var goal = -1;
        for (var i = 0; i < nodes.Count; i++) { if (nodes[i].Id == from) { start = i; } if (nodes[i].Id == to) { goal = i; } }
        if (start < 0 || goal < 0) { return long.MaxValue; }
        var distance = new long[nodes.Count]; var done = new bool[nodes.Count];
        Array.Fill(distance, long.MaxValue); distance[start] = 0;
        for (var step = 0; step < nodes.Count; step++)
        {
            var at = -1;
            for (var i = 0; i < nodes.Count; i++)
            { if (!done[i] && distance[i] != long.MaxValue && (at < 0 || distance[i] < distance[at] || distance[i] == distance[at] && nodes[i].Id < nodes[at].Id)) { at = i; } }
            if (at < 0) { break; }
            if (at == goal) { return distance[at]; }
            done[at] = true;
            for (var i = 0; i < nodes.Count; i++)
            {
                if (done[i] || !rubble && (nodes[at].Rubble || nodes[i].Rubble) ||
                    size > 1 && (nodes[at].Def.Archetype.Tunnel!.Size < 2 || nodes[i].Def.Archetype.Tunnel!.Size < 2) || !World.TunnelLinked(nodes[at], nodes[i])) { continue; }
                var ticks = Fixed.Sqrt(Distance(Cell.Of(nodes[at].Pos), Cell.Of(nodes[i].Pos))) / Math.Max(1, nodes[at].Def.Archetype.Tunnel!.Speed.Raw);
                var cost = distance[at] + Math.Max(1, ticks);
                if (cost < distance[i]) { distance[i] = cost; }
            }
        }
        return long.MaxValue;
    }

    private bool TryTunnel(World w, Mission m)
    {
        if (m.Tunnel is not null) { UpdateTunnel(w, m); return true; }
        if (Doctrine?.Tunnels is not { } p || w.Tick < m.NextTunnelTry || w.Tick < m.NextOrder ||
            m.Kind is not (AiMissionKind.Assault or AiMissionKind.Recover) ||
            m.Members.Any(id => _ordered.BinarySearch(id) >= 0) ||
            m.Kind != AiMissionKind.Recover && m.Members.Any(id => _visible.Any(e => Near(Cell.Of(Own(id)!.Pos), Cell.Of(e.Pos), _rules.AssaultCells)))) { return false; }
        m.NextTunnelTry = w.Tick + (uint)p.RetryTicks;
        var route = FindTunnelRoute(w, m, m.Objective, true);
        if (route is null) { return false; }
        if (!Emit(w, new Command { Kind = CommandKind.Enter, Units = m.Members, Target = route.Entry }, m.Id, "tunnel-board")) { return false; }
        m.Tunnel = new TunnelTrip(route.Entry, route.Exit, w.Tick, w.Tick + (uint)p.TravelTicks)
        { NextOrder = w.Tick + World.InputDelay + 1 };
        m.RegroupPoint = null; m.RegroupUntil = 0;
        Trace(w, m.Id, "tunnel-route-selected");
        return true;
    }

    private void UpdateTunnel(World w, Mission m)
    {
        var trip = m.Tunnel!; var p = Doctrine!.Tunnels!;
        var entry = Own(trip.Entry); var exit = Own(trip.Exit);
        var exitDanger = exit is null ? 0 : Danger(w, Cell.Of(World.TunnelDoor(exit)), false);
        var strength = m.Members.Sum(id => Own(id) is { } u ? (long)Strength(u) : 0);
        if (exitDanger * 100 > Math.Max(1, strength) * p.DangerPercent || w.Tick >= trip.Until || w.Tick >= m.Deadline || entry is null || entry.Rubble || exit is null || exit.Rubble ||
            trip.Stage == 0 && w.Tick >= trip.Started + (uint)p.BoardTicks)
        { AbortTunnel(w, m); }
        if (w.Tick < trip.NextOrder) { return; }
        if (trip.Stage == 3) { EvacuateTunnel(w, m); return; }
        var underground = m.Members.Where(id => Own(id) is { InTunnel: true }).ToArray();
        if (trip.Stage == 0)
        {
            if (underground.Length == m.Members.Count)
            {
                if (Emit(w, new Command { Kind = CommandKind.TunnelGo, Units = underground, Target = trip.Exit, On = true }, m.Id, "tunnel-transit"))
                { trip.Stage = 2; trip.NextOrder = w.Tick + (uint)Math.Max(p.RetryTicks, World.InputDelay + 1); }
            }
            else
            {
                var waiting = m.Members.Where(id => Own(id) is { InTunnel: false, Inside: 0 } u && (u.Order.Kind != OrderKind.Enter || u.Order.Target != trip.Entry)).ToArray();
                if (waiting.Length > 0 && Emit(w, new Command { Kind = CommandKind.Enter, Units = waiting, Target = trip.Entry }, m.Id, "tunnel-retry-boarding"))
                { trip.NextOrder = w.Tick + (uint)Math.Max(p.RetryTicks, World.InputDelay + 1); }
            }
        }
        else if (underground.Length == 0) { FinishTunnel(w, m, "tunnel-arrived"); }
        else
        {
            var idle = underground.Where(id => Own(id) is { } u && (u.TunnelTo != trip.Exit || !u.TunnelOut)).ToArray();
            if (idle.Length > 0 && Emit(w, new Command { Kind = CommandKind.TunnelGo, Units = idle, Target = trip.Exit, On = true }, m.Id, "tunnel-retry-transit"))
            { trip.NextOrder = w.Tick + (uint)Math.Max(p.RetryTicks, World.InputDelay + 1); }
        }
    }

    private void AbortTunnel(World w, Mission m)
    {
        if (m.Tunnel is not { Stage: not 3 } trip) { return; }
        trip.Stage = 3; trip.NextOrder = w.Tick;
        Trace(w, m.Id, "tunnel-evacuating");
    }

    private void EvacuateTunnel(World w, Mission m)
    {
        var trip = m.Tunnel!; var p = Doctrine!.Tunnels!;
        if (!trip.BoardingCancelled)
        {
            var outside = m.Members.Where(id => Own(id) is { InTunnel: false }).ToArray();
            if (outside.Any(id => _ordered.BinarySearch(id) >= 0)) { return; }
            if (outside.Length > 0 && !Emit(w, new Command { Kind = CommandKind.Stop, Units = outside }, m.Id, "tunnel-cancel-boarding")) { return; }
            trip.BoardingCancelled = true; trip.NextOrder = w.Tick + World.InputDelay + 1;
            return;
        }
        var inside = m.Members.Where(id => Own(id) is { InTunnel: true }).ToArray();
        if (inside.Length == 0) { FinishTunnel(w, m, "tunnel-evacuated"); return; }
        var nodes = w.TunnelNodes(Player).Take(p.MaxNodes).ToArray();
        foreach (var id in inside)
        {
            var unit = Own(id)!;
            var from = unit.TunnelNext != 0 ? unit.TunnelNext : unit.Tunnel;
            var candidate = nodes.Where(n => !n.Rubble && n.Def.Archetype.Tunnel!.Size >= unit.Def.Archetype.TunnelSize &&
                    TunnelTicks(nodes, from, n.Id, unit.Def.Archetype.TunnelSize, true) != long.MaxValue)
                .OrderBy(n => Danger(w, Cell.Of(n.Pos), false)).ThenBy(n => Distance(Cell.Of(n.Pos), Home)).ThenBy(n => n.Id).FirstOrDefault();
            if (candidate is null) { continue; } // physically trapped: retain ownership and retry when the network changes
            if (unit.TunnelTo == candidate.Id && unit.TunnelOut) { continue; }
            Emit(w, new Command { Kind = CommandKind.TunnelGo, Units = [id], Target = candidate.Id, On = true }, m.Id, "tunnel-emergency-exit");
        }
        trip.NextOrder = w.Tick + (uint)Math.Max(p.RetryTicks, World.InputDelay + 1);
    }

    private void FinishTunnel(World w, Mission m, string reason)
    {
        m.Tunnel = null; m.NextTunnelTry = w.Tick + (uint)Doctrine!.Tunnels!.RetryTicks;
        m.LastKind = 0; m.NextOrder = w.Tick; m.BestDistance = long.MaxValue; m.ProgressAt = w.Tick;
        Trace(w, m.Id, reason);
    }

    private bool TunnelPlacementLinked(World w, AiUnitGoal goal, UnitDef def, Cell center)
    {
        if (Doctrine?.Tunnels is null || def.Archetype.Tunnel is not { } proposed) { return true; }
        var existing = _own.Where(u => u.IsBuilt && !u.Rubble && u.Def.Archetype.Tunnel is not null).ToArray();
        existing = existing.Where(u => proposed.Size == 1 || u.Def.Archetype.Tunnel!.Size >= 2).ToArray();
        if (existing.Length == 0) { return !goal.Forward; }
        return existing.Any(u => World.Within(center.Center(), u.Pos, proposed.LinkRange < u.Def.Archetype.Tunnel!.LinkRange ? proposed.LinkRange : u.Def.Archetype.Tunnel.LinkRange));
    }
}
