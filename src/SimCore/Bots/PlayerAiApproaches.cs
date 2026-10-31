using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private bool ResolveApproach(World w, Mission m, Cell cell, out Cell at)
    {
        if (Reachable(w, Own(m.Members[0])!, cell, out var direct) && m.Members.All(id => Reachable(w, Own(id)!, direct, out _)))
        { at = direct; return true; }
        at = cell;
        // Staging across a disconnected surface may still be valid through an owned compatible tunnel route.
        var routing = w.Routing;
        var kind = routing.ClassOf(Own(m.Members[0])!.Def);
        var nearest = kind < 0 ? -1 : routing.NearestPassable(kind, cell, _rules.ReachSearchCells);
        if (nearest < 0) { return false; }
        at = routing.CellAt(nearest);
        return MissionReachable(w, m, at);
    }

    private long ApproachCost(World w, Mission m, Cell at, Cell wanted)
    {
        var foundation = _rules.Foundation!;
        var cost = Danger(w, at, false) * foundation.DangerWeight + Fixed.Sqrt(Distance(at, wanted)) / Fixed.OneRaw;
        if (foundation.OperationControl is not { } p) { return cost; }
        var from = Cell.Of(Own(m.Members[0])!.Pos);
        var exposure = Danger(w, at, false);
        for (var sample = 1; sample <= p.RouteSamples; sample++)
        {
            var point = new Cell(from.X + (int)((long)(at.X - from.X) * sample / (p.RouteSamples + 1)),
                from.Y + (int)((long)(at.Y - from.Y) * sample / (p.RouteSamples + 1)));
            exposure = Math.Max(exposure, Danger(w, point, false));
        }
        cost += exposure * foundation.DangerWeight + Fixed.Sqrt(Distance(from, at)) / Fixed.OneRaw * p.ApproachTravelWeight;
        if (p.SeparationCells == 0) { return cost; }
        foreach (var other in _missions)
        {
            if (other.Done || other.Id == m.Id || other.Operation == 0 || other.Operation != m.Operation || other.Phase != AiMissionPhase.Staging) { continue; }
            var apart = Fixed.Sqrt(Distance(at, other.Objective)) / Fixed.OneRaw;
            if (apart < p.SeparationCells) { cost += (p.SeparationCells - apart) * p.SeparationPenalty / p.SeparationCells; }
        }
        return cost;
    }
}
