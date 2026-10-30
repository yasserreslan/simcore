using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private void Economy(World w)
    {
        if (_plan is null) { return; }
        if (_rules.Foundation?.BuildPlanning is { } planning &&
            (_situation.Posture == AiPosture.Rebuild || _sites.Any(s => w.Tick >= s.ProgressAt + (uint)planning.SiteStallTicks)))
        { Construct(w); Produce(w); }
        else { Produce(w); Construct(w); }
    }

    private int CountDefinition(int def)
    {
        var n = _own.Count(u => u.Def.Index == def && !u.Rubble);
        foreach (var f in _own)
        {
            n += f.ProductionQueue.Count(d => d == def);
            n += f.Bays.Count(b => b.Def == def);
            n += f.Doors.Count(d => d.Def == def);
        }
        return n + _pending.Count(p => p.Def == def && p.Kind is CommandKind.Produce or CommandKind.Build);
    }

    private void Produce(World w)
    {
        var population = _own.Count + _pending.Count(p => p.Kind == CommandKind.Build);
        foreach (var f in _own)
        {
            foreach (var d in f.ProductionQueue) { population += w.Match.Defs[d].Crew is null ? 1 : 2; }
            foreach (var b in f.Bays) { if (b.Def >= 0) { population += w.Match.Defs[b.Def].Crew is null ? 1 : 2; } }
            foreach (var door in f.Doors) { if (door.Def >= 0) { population += w.Match.Defs[door.Def].Crew is null ? 1 : 2; } }
        }
        foreach (var p in _pending)
        { if (p.Kind == CommandKind.Produce) { population += w.Match.Defs[p.Def].Crew is null ? 1 : 2; } }
        if (population >= _rules.ArmyCap) { return; }
        var player = w.Match.Players[Player - 1];
        foreach (var goal in ProductionGoals(w))
        {
            var def = player.Find(w.Content, goal.Unit);
            if (def is null || def.IsStructure || def.Strike is not null ||
                (_rules.Foundation is null && (def.Layer != Layers.Ground || def.Archetype.Flight is not null)) ||
                CountDefinition(def.Index) >= Desired(goal, w)) { continue; }
            foreach (var f in _own)
            {
                if (!f.IsBuilt || f.Rubble || f.Def.Archetype.Production is not { } p || !p.Builds(def.Tags) ||
                    (long)f.HP * 100 < (long)f.Def.MaxHp * p.MinHpPercent || _pending.Any(q => q.Source == f.Id)) { continue; }
                if (!ProductionCompatible(w, f, def, p)) { continue; }
                var queued = f.ProductionQueue.Count + f.Bays.Count(b => b.Def >= 0) + f.Doors.Count(d => d.Def >= 0);
                if (queued >= Math.Min(p.QueueMax, _rules.FactoryQueue)) { continue; }
                // Production creates a driver too; count that slot before committing to a crewed vehicle.
                if (population + (def.Crew is null ? 1 : 2) > _rules.ArmyCap) { continue; }
                if (Emit(w, new Command { Kind = CommandKind.Produce, Units = [f.Id], Unit = goal.Unit }, 0, "production-deficit"))
                { _pending.Add(new Pending(f.Id, def.Index, CommandKind.Produce, w.Tick + World.InputDelay)); }
                return; // one production order per decision; no queue flooding
            }
        }
    }

    private void Construct(World w)
    {
        if (_rules.Foundation?.BuildPlanning is not null) { PlanConstruction(w); return; }
        var builders = _own.Where(u => Mobile(u) && u.Def.Builder is not null && Free(u) && !Reserved(u.Id) && !Resting(u.Id) && _ordered.BinarySearch(u.Id) < 0).ToArray();
        if (builders.Length == 0) { return; }
        // Resume an existing useful site before creating another one. Do not duplicate an active builder assignment.
        foreach (var site in _own)
        {
            if (site.IsBuilt || site.Rubble || site.Def.Archetype.Construction is null ||
                !_plan!.Construction.Any(g => w.Match.Players[Player - 1].Find(w.Content, g.Unit)?.Index == site.Def.Index) ||
                _own.Any(u => u.Order.Kind == OrderKind.Build && u.Order.Target == site.Id) ||
                _pending.Any(p => p.Kind == CommandKind.Resume && p.Def == site.Def.Index)) { continue; }
            var builder = builders.FirstOrDefault(u => Reachable(w, u, Cell.Of(site.Pos), out _));
            if (builder is not null && Emit(w, new Command { Kind = CommandKind.Resume, Units = [builder.Id], Target = site.Id }, 0, "resume-site"))
            { _pending.Add(new Pending(builder.Id, site.Def.Index, CommandKind.Resume, w.Tick + World.InputDelay)); return; }
        }
        if (w.Tick < _nextBuild || _pending.Any(p => p.Kind is CommandKind.Build or CommandKind.Resume) ||
            _own.Any(u => !u.IsBuilt && u.Def.Archetype.Construction is not null)) { return; }
        var player = w.Match.Players[Player - 1];
        foreach (var goal in ConstructionGoals(w))
        {
            var def = player.Find(w.Content, goal.Unit);
            if (def?.Archetype.Construction is null || !def.IsStructure || def.Archetype.Locked || CountDefinition(def.Index) >= Desired(goal, w) ||
                def.Archetype.FootprintW > w.Grid.Width || def.Archetype.FootprintH > w.Grid.Height) { continue; }
            var cells = 2 * _rules.BuildRadiusCells / _rules.BuildSpacingCells + 1;
            var anchor = _rules.Foundation is { Expansion: true } foundation && Directive is not { ForwardBuild: false } && goal.Forward && _situation.Front != Home &&
                _situation.Posture is AiPosture.Expand or AiPosture.Attack ? Toward(w, Home, _situation.Front, foundation.ExpansionCells) : Home;
            for (var checks = 0; checks < _rules.BuildChecksPerDecision; checks++)
            {
                var serial = _buildCursor++;
                var index = serial % (cells * cells);
                var facing = _rules.Foundation is null ? 0 : serial / (cells * cells) % 4 * 8;
                var at = new Cell(anchor.X - _rules.BuildRadiusCells + index % cells * _rules.BuildSpacingCells,
                    anchor.Y - _rules.BuildRadiusCells + index / cells * _rules.BuildSpacingCells);
                var width = facing % 16 == 0 ? def.Archetype.FootprintW : def.Archetype.FootprintH;
                var height = facing % 16 == 0 ? def.Archetype.FootprintH : def.Archetype.FootprintW;
                var centre = new Cell(at.X + width / 2, at.Y + height / 2);
                if (!w.Grid.Contains(centre) || !Sees(w, centre) ||
                    _visible.Any(e => Near(Cell.Of(e.Pos), centre, _rules.DefendCells))) { continue; }
                var builder = builders.FirstOrDefault(u => Reachable(w, u, centre, out _));
                if (builder is null || !w.CanPlace(def, at, facing, [builder])) { continue; }
                if (Emit(w, new Command { Kind = CommandKind.Build, Units = [builder.Id], Unit = goal.Unit, Cell = at, Facing = facing }, 0, "construction-deficit"))
                {
                    _pending.Add(new Pending(builder.Id, def.Index, CommandKind.Build, w.Tick + World.InputDelay));
                    _nextBuild = w.Tick + World.InputDelay + (uint)_rules.BuildRetryTicks;
                }
                return;
            }
            return; // preserve placement cursor and bounded work for the first unmet goal
        }
    }

    private IEnumerable<AiUnitGoal> ProductionGoals(World w)
    {
        if (_rules.Foundation is null) { return _plan!.Production; }
        return _plan!.Production.Select((goal, index) => (Goal: goal, Index: index,
                Def: w.Match.Players[Player - 1].Find(w.Content, goal.Unit)))
            .Where(g => g.Def is not null && GoalPriority(w, g.Goal, g.Def) > 0 && Desired(g.Goal, w) > CountDefinition(g.Def.Index))
            .OrderByDescending(g => GoalPriority(w, g.Goal, g.Def!) * (Desired(g.Goal, w) - CountDefinition(g.Def!.Index)) * 1000 / Math.Max(1, Desired(g.Goal, w)))
            .ThenBy(g => g.Index).Select(g => g.Goal);
    }

    private IEnumerable<AiUnitGoal> ConstructionGoals(World w)
    {
        if (PosturePolicy is null && _rules.Foundation?.BuildPlanning is null) { return _plan!.Construction; }
        return _plan!.Construction.Select((goal, index) => (Goal: goal, Index: index,
                Def: w.Match.Players[Player - 1].Find(w.Content, goal.Unit)))
            .Where(g => g.Def is not null && GoalPriority(w, g.Goal, g.Def) > 0 && Desired(g.Goal, w) > CountDefinition(g.Def.Index))
            .OrderByDescending(g => (GoalPriority(w, g.Goal, g.Def!) + ProducerDemand(w, g.Def!)) * (Desired(g.Goal, w) - CountDefinition(g.Def!.Index)) * 1000 / Math.Max(1, Desired(g.Goal, w)))
            .ThenBy(g => g.Index).Select(g => g.Goal);
    }

    private bool ProductionCompatible(World w, Unit f, UnitDef def, ProductionSpec p)
    {
        if (w.ExclusiveOf(Player, def.Archetype) is { Allowed: false }) { return false; }
        if (!p.IntoStation) { return def.Layer == Layers.Ground && def.Archetype.Flight is null; }
        if (_rules.Foundation is not { Aircraft: true } || def.Archetype.Flight is null) { return false; }
        if (f.Def.Archetype.Helipad is not null)
        {
            return def.Archetype.Flight.Rotor is not null && !_own.Any(u => u.Heli?.Pad == f.Id);
        }
        var field = w.Airfields.FirstOrDefault(a => a.Facility == f.Id || a.Hangar == f.Id);
        return field is not null && Own(field.Facility) is { Rubble: false, IsBuilt: true } && def.Archetype.Aircraft is { } ac &&
            w.StationsFor(field, ac).Any(s => s.Occupant == 0 && s.ReservedBy == 0);
    }
}
