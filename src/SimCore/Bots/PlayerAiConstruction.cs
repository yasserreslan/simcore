using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Detached placement search state, indexed by the original construction policy list.</summary>
public sealed record AiBuildPlanMemory(int Goal, Cell Anchor, int Cursor, uint NextTry, int FailedSweeps);
/// <summary>Detached progress and retry clocks for a configured unfinished site.</summary>
public sealed record AiSiteMemory(uint Site, int Work, uint ProgressAt, uint NextTry);

public sealed partial class PlayerAi
{
    private readonly List<BuildPlan> _buildPlans = [];
    private readonly List<SitePlan> _sites = [];
    /// <summary>Independent bounded placement cursors and retry clocks.</summary>
    public IReadOnlyList<AiBuildPlanMemory> BuildPlans => _buildPlans.Select(p => new AiBuildPlanMemory(p.Goal, p.Anchor, p.Cursor, p.NextTry, p.FailedSweeps)).ToArray();
    /// <summary>Observed friendly construction progress, without live object references.</summary>
    public IReadOnlyList<AiSiteMemory> ConstructionSites => _sites.Select(s => new AiSiteMemory(s.Site, s.Work, s.ProgressAt, s.NextTry)).ToArray();

    private bool PlannedSite(World w, Unit u) => !u.IsBuilt && !u.Rubble && u.Def.Archetype.Construction is not null &&
        _plan is not null && _plan.Construction.Any(g => w.Match.Players[Player - 1].Find(w.Content, g.Unit)?.Index == u.Def.Index) &&
        !(u.Def.Archetype.Airfield is { Role: AirfieldRole.Segment } && w.AirfieldOf(u) is { LeadRow: not 0 });

    private void ObserveConstruction(World w)
    {
        if (_rules.Foundation?.BuildPlanning is null) { return; }
        _sites.RemoveAll(s => Own(s.Site) is not { } u || !PlannedSite(w, u));
        foreach (var u in _own)
        {
            if (!PlannedSite(w, u)) { continue; }
            var site = _sites.FirstOrDefault(s => s.Site == u.Id);
            if (site is null)
            {
                site = new SitePlan(u.Id, u.Work, w.Tick);
                var at = _sites.FindIndex(s => s.Site > u.Id);
                if (at < 0) { _sites.Add(site); } else { _sites.Insert(at, site); }
            }
            else if (u.Work > site.Work)
            { site.Work = u.Work; site.ProgressAt = w.Tick; site.NextTry = 0; }
        }
    }

    private bool PreserveBuilder(World w, Unit u)
    {
        if (_rules.Foundation?.BuildPlanning is not { BuilderReserve: > 0 } p || u.Def.Builder is null || _plan is null ||
            !(_sites.Count > 0 || _plan.Construction.Any(g => w.Match.Players[Player - 1].Find(w.Content, g.Unit) is { } d &&
                CountDefinition(d.Index) < Desired(g, w)))) { return false; }
        return _own.Where(v => Mobile(v) && v.Def.Builder is not null && (v.Id == u.Id || !SpecialistReserved(v.Id)))
            .Take(p.BuilderReserve).Any(v => v.Id == u.Id);
    }

    private void PlanConstruction(World w)
    {
        var p = _rules.Foundation!.BuildPlanning!;
        // Cancellation requires only a configured site, so it must still work after every worker has been lost.
        foreach (var s in _sites)
        {
            if (!p.CancelAbandoned || w.Tick < s.ProgressAt + (uint)p.AbandonTicks || w.Tick < s.NextTry ||
                _pending.Any(q => q.Target == s.Site) || Own(s.Site) is not { } site) { continue; }
            if (Emit(w, new Command { Kind = CommandKind.CancelBuild, Units = [site.Id], Target = site.Id }, 0, "cancel-stalled-site"))
            {
                _pending.Add(new Pending(site.Id, site.Def.Index, CommandKind.CancelBuild, w.Tick + World.InputDelay, site.Id));
                s.NextTry = w.Tick + World.InputDelay + (uint)p.SiteRetryTicks;
                DelayDefinition(w, site.Def.Index, p.FailedSweepTicks);
            }
            return;
        }
        var builders = _own.Where(u => Mobile(u) && u.Def.Builder is not null && Free(u) && !Reserved(u.Id) && !Resting(u.Id) &&
            _ordered.BinarySearch(u.Id) < 0 && !_pending.Any(q => q.Source == u.Id)).ToArray();
        foreach (var s in _sites)
        {
            if (w.Tick < s.NextTry || _pending.Any(q => q.Target == s.Site) || Own(s.Site) is not { } site) { continue; }
            var assigned = _own.Where(u => Mobile(u) && u.Def.Builder is not null && u.Order.Kind == OrderKind.Build && u.Order.Target == site.Id).ToArray();
            var stalled = w.Tick >= s.ProgressAt + (uint)p.SiteStallTicks;
            if (assigned.Length > 0 && !stalled) { continue; }
            var worker = builders.Concat(stalled ? assigned : [])
                .OrderBy(u => u.Order.Kind == OrderKind.Build && u.Order.Target == site.Id ? 1 : 0).ThenBy(u => u.Id)
                .FirstOrDefault(u => !_pending.Any(q => q.Source == u.Id) && _ordered.BinarySearch(u.Id) < 0 && SiteAccess(w, u, site));
            if (worker is null) { s.NextTry = w.Tick + (uint)p.SiteRetryTicks; continue; }
            if (Emit(w, new Command { Kind = CommandKind.Resume, Units = [worker.Id], Target = site.Id }, 0, "retry-construction-site"))
            {
                _pending.Add(new Pending(worker.Id, site.Def.Index, CommandKind.Resume, w.Tick + World.InputDelay, site.Id));
                s.NextTry = w.Tick + World.InputDelay + (uint)p.SiteRetryTicks;
            }
            return;
        }
        if (builders.Length == 0 || w.Tick < _nextBuild || _pending.Any(q => q.Kind is CommandKind.Build or CommandKind.Resume or CommandKind.CancelBuild)) { return; }
        var unfinished = _own.Count(u => !u.IsBuilt && !u.Rubble && u.Def.Archetype.Construction is not null &&
            !(u.Def.Archetype.Airfield is { Role: AirfieldRole.Segment } && w.AirfieldOf(u) is { LeadRow: not 0 }));
        if (unfinished >= p.MaxSites || _sites.Any(s => w.Tick < s.ProgressAt + (uint)p.SiteStallTicks)) { return; }
        var remaining = _rules.BuildChecksPerDecision;
        var goals = 0;
        foreach (var goal in ConstructionGoals(w))
        {
            if (remaining == 0 || goals >= p.GoalsPerDecision) { break; }
            var def = w.Match.Players[Player - 1].Find(w.Content, goal.Unit);
            if (def?.Archetype.Construction is null || !def.IsStructure || def.Archetype.Locked || CountDefinition(def.Index) >= Desired(goal, w)) { continue; }
            var index = GoalIndex(goal.Unit);
            var anchor = BuildAnchor(w, goal);
            var plan = _buildPlans.FirstOrDefault(b => b.Goal == index);
            if (plan is null)
            {
                plan = new BuildPlan(index, anchor);
                var at = _buildPlans.FindIndex(b => b.Goal > index);
                if (at < 0) { _buildPlans.Add(plan); } else { _buildPlans.Insert(at, plan); }
            }
            // A changed base/front invalidates geometry, but does not erase a failure cooldown.
            if (plan.Anchor != anchor) { plan.Anchor = anchor; plan.Cursor = 0; }
            if (w.Tick < plan.NextTry) { continue; }
            goals++;
            var side = 2 * _rules.BuildRadiusCells / _rules.BuildSpacingCells + 1;
            var plane = side * side;
            var total = plane * 4;
            var slice = Math.Min(remaining, p.CandidatesPerGoal);
            for (var i = 0; i < slice; i++)
            {
                remaining--;
                var serial = plan.Cursor;
                plan.Cursor = (plan.Cursor + 1) % total;
                var offset = BuildOffset(serial % plane);
                var facing = serial / plane * 8;
                var at = new Cell(anchor.X + offset.X * _rules.BuildSpacingCells,
                    anchor.Y + offset.Y * _rules.BuildSpacingCells);
                var width = facing % 16 == 0 ? def.Archetype.FootprintW : def.Archetype.FootprintH;
                var height = facing % 16 == 0 ? def.Archetype.FootprintH : def.Archetype.FootprintW;
                var center = new Cell(at.X + width / 2, at.Y + height / 2);
                if (w.Grid.Contains(center) && Sees(w, center) && !_visible.Any(e => Near(Cell.Of(e.Pos), center, _rules.DefendCells)))
                {
                    var builder = builders.FirstOrDefault(u => Reachable(w, u, center, out _) && w.CanPlace(def, at, facing, [u]));
                    if (builder is not null && Emit(w, new Command { Kind = CommandKind.Build, Units = [builder.Id], Unit = goal.Unit, Cell = at, Facing = facing }, 0, "scheduled-construction"))
                    {
                        _pending.Add(new Pending(builder.Id, def.Index, CommandKind.Build, w.Tick + World.InputDelay));
                        _nextBuild = w.Tick + World.InputDelay + (uint)_rules.BuildRetryTicks;
                        plan.NextTry = _nextBuild;
                        return;
                    }
                }
                if (plan.Cursor == 0)
                {
                    plan.FailedSweeps = Math.Min(1_000_000, plan.FailedSweeps + 1);
                    plan.NextTry = w.Tick + (uint)p.FailedSweepTicks;
                    Trace(w, 0, "construction-sweep-failed");
                    break;
                }
            }
            if (plan.Cursor != 0) { plan.NextTry = w.Tick + (uint)p.SliceRetryTicks; }
        }
    }

    private bool SiteAccess(World w, Unit worker, Unit site)
    {
        var edge = World.AimPoint(worker.Pos, site);
        var dx = Math.Sign(worker.Pos.X.Raw - site.Pos.X.Raw);
        var dy = Math.Sign(worker.Pos.Y.Raw - site.Pos.Y.Raw);
        var outside = new Vec(Fixed.FromRaw(edge.X.Raw + dx * Fixed.OneRaw), Fixed.FromRaw(edge.Y.Raw + dy * Fixed.OneRaw));
        return Reachable(w, worker, Clamp(w, Cell.Of(outside)), out _);
    }

    private Cell BuildAnchor(World w, AiUnitGoal goal) => _rules.Foundation is { Expansion: true } foundation &&
        Directive is not { ForwardBuild: false } && goal.Forward && _situation.Front != Home &&
        _situation.Posture is AiPosture.Expand or AiPosture.Attack ? Toward(w, Home, _situation.Front, foundation.ExpansionCells) : Home;

    private int GoalIndex(string unit)
    {
        for (var i = 0; i < _plan!.Construction.Count; i++) { if (_plan.Construction[i].Unit == unit) { return i; } }
        return -1;
    }

    private void DelayDefinition(World w, int def, int ticks)
    {
        for (var i = 0; i < _plan!.Construction.Count; i++)
        {
            var goal = _plan.Construction[i];
            if (w.Match.Players[Player - 1].Find(w.Content, goal.Unit)?.Index != def) { continue; }
            var plan = _buildPlans.FirstOrDefault(p => p.Goal == i);
            if (plan is null)
            {
                plan = new BuildPlan(i, BuildAnchor(w, goal));
                var at = _buildPlans.FindIndex(p => p.Goal > i);
                if (at < 0) { _buildPlans.Add(plan); } else { _buildPlans.Insert(at, plan); }
            }
            plan.NextTry = w.Tick + (uint)ticks;
        }
    }

    private static Cell BuildOffset(int index)
    {
        if (index == 0) { return default; }
        var ring = (int)((Fixed.Sqrt(index) + 1) / 2);
        var first = (2 * ring - 1) * (2 * ring - 1);
        var edge = (index - first) / (2 * ring);
        var step = (index - first) % (2 * ring);
        return edge switch
        {
            0 => new Cell(-ring + step, -ring), 1 => new Cell(ring, -ring + step),
            2 => new Cell(ring - step, ring), _ => new Cell(-ring, ring - step),
        };
    }

    private int ProducerDemand(World w, UnitDef facility)
    {
        if (_rules.Foundation?.BuildPlanning is not { } p || facility.Archetype.Production is not { } production || _plan is null) { return 0; }
        foreach (var goal in _plan.Production)
        {
            var def = w.Match.Players[Player - 1].Find(w.Content, goal.Unit);
            if (def is null || def.IsStructure || def.Strike is not null || !production.Builds(def.Tags) || CountDefinition(def.Index) >= Desired(goal, w)) { continue; }
            if (!_own.Any(f => f.IsBuilt && !f.Rubble && f.Def.Archetype.Production is { } existing && existing.Builds(def.Tags) &&
                (long)f.HP * 100 >= (long)f.Def.MaxHp * existing.MinHpPercent && ProductionCompatible(w, f, def, existing)))
            { return p.MissingProducerBonus; }
        }
        return 0;
    }

    private sealed class BuildPlan(int goal, Cell anchor)
    { public readonly int Goal = goal; public Cell Anchor = anchor; public int Cursor, FailedSweeps; public uint NextTry; }
    private sealed class SitePlan(uint site, int work, uint tick)
    { public readonly uint Site = site; public int Work = work; public uint ProgressAt = tick, NextTry; }
}
