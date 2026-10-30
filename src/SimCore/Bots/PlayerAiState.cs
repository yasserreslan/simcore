using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    /// <summary>A deterministic compatibility digest of every policy field, excluding diagnostic capacity.</summary>
    public ulong PolicyDigest => DigestPolicy();

    /// <summary>Copies all decision-relevant planner memory at the world's current tick. Does not capture the World or pending commands.</summary>
    public AiPlannerState CaptureState()
    {
        var w = _world ?? throw new InvalidOperationException("The bot must be bound by Think before it can be checkpointed.");
        return new AiPlannerState(new AiStateIdentity(4, Player, w.Tick, w.Hash(), w.Match.Digest, w.Content.Hash, PolicyDigest),
            new AiStateClock(_nextDecision, _nextStrategy, _nextBuild, _serial, _operationSerial, _jobSerial, _missionCursor, _buildCursor, Home, _postureUntil),
            new AiProbeMemory(_probes.ToArray(), _seen.ToArray(), _retry.ToArray()),
            _memory.Select(m => new AiStructureMemory(m.Id, m.Cell)).ToArray(), _intel.ToArray(), _situation, _objectives.ToArray(), _outcomes.ToArray(),
            _rest.Select(r => new AiRestMemory(r.Unit, r.Until)).ToArray(),
            _pending.Select(p => new AiPendingMemory(p.Source, p.Def, p.Kind, p.Due, p.Target)).ToArray(),
            _missions.Select(m => new AiMissionMemory(m.Id, m.Kind, m.Phase, m.Started, m.Deadline, m.ProgressAt, m.NextOrder,
                m.BestDistance, m.Objective, m.LastCell, m.LastKind, m.LastTarget, m.Contact, m.Operation, m.Approach,
                m.FinalObjective, m.Probe, m.LaunchMembers, m.Done, m.OutcomeRecorded, m.Members.ToArray(), m.RegroupPoint, m.RegroupUntil, m.NextRegroup)).ToArray(),
            _cargo.Select(c => new AiCargoMemory(c.Truck, c.Store, c.Kind, c.Until, c.NextOrder, c.Done)).ToArray(),
            _operations.Select(o => new AiOperationMemory(o.Id, o.Subject, o.Objective, o.ReleaseAt, o.Deadline, o.Phase)).ToArray(),
            _specialists.Select(j => new AiSpecialistMemory(j.Id, j.Kind, j.Actor, j.Target, j.Point, j.LastCell, j.Pickup,
                j.Since, j.Until, j.NextOrder, j.LastTarget, j.LastKind, j.Stage, j.Probe, j.Done, j.Passengers.ToArray())).ToArray(), BuildPlans, ConstructionSites);
    }

    /// <summary>Restores a checkpoint into a fresh bot after checking identity, limits, IDs, enums, and exclusive ownership.</summary>
    public void RestoreState(World w, AiPlannerState state)
    {
        if (_world is not null) { throw new InvalidOperationException("Restore requires a fresh PlayerAi instance."); }
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(w, state); // reject before changing any planner fields
        var c = state.Clock;
        _world = w;
        _plan = _rules.Factions.FirstOrDefault(f => f.Faction == w.Match.Players[Player - 1].Faction.Id);
        _postureUntil = c.PostureUntil;
        _nextDecision = c.NextDecision; _nextStrategy = c.NextStrategy; _nextBuild = c.NextBuild;
        _serial = c.MissionSerial; _operationSerial = c.OperationSerial; _jobSerial = c.JobSerial;
        _missionCursor = c.MissionCursor; _buildCursor = c.BuildCursor; Home = c.Home;
        _probes = state.Probes.Cells.ToArray(); _seen = state.Probes.Seen.ToArray(); _retry = state.Probes.Retry.ToArray();
        _memory.AddRange(state.Structures.Select(m => new Contact(m.Id, m.Cell)));
        _intel.AddRange(state.Intelligence); _situation = state.Situation;
        _objectives.AddRange(state.Objectives); _outcomes.AddRange(state.Outcomes);
        _rest.AddRange(state.Rest.Select(r => (r.Unit, r.Until)));
        _pending.AddRange(state.Pending.Select(p => new Pending(p.Source, p.Def, p.Kind, p.Due, p.Target)));
        foreach (var m in state.Missions)
        {
            var mission = new Mission(m.Id, m.Kind, m.Started)
            {
                RegroupPoint = m.RegroupPoint, RegroupUntil = m.RegroupUntil, NextRegroup = m.NextRegroup,
                Phase = m.Phase, Deadline = m.Deadline, ProgressAt = m.ProgressAt, NextOrder = m.NextOrder,
                BestDistance = m.BestDistance, Objective = m.Objective, LastCell = m.LastCell, LastKind = m.LastKind,
                LastTarget = m.LastTarget, Contact = m.Contact, Operation = m.Operation, Approach = m.Approach,
                FinalObjective = m.FinalObjective, Probe = m.Probe, LaunchMembers = m.LaunchMembers, Done = m.Done, OutcomeRecorded = m.OutcomeRecorded,
            };
            mission.Members.AddRange(m.Members); _missions.Add(mission);
        }
        _cargo.AddRange(state.Cargo.Select(j => new CargoJob(j.Truck, j.Store, j.Kind, j.Until, j.NextOrder) { Done = j.Done }));
        _operations.AddRange(state.Operations.Select(o => new Operation(o.Id, o.Subject, o.Objective, o.ReleaseAt, o.Deadline) { Phase = o.Phase }));
        _buildPlans.AddRange(state.BuildPlans!.Select(p => new BuildPlan(p.Goal, p.Anchor) { Cursor = p.Cursor, NextTry = p.NextTry, FailedSweeps = p.FailedSweeps }));
        _sites.AddRange(state.Sites!.Select(s => new SitePlan(s.Site, s.Work, s.ProgressAt) { NextTry = s.NextTry }));
        foreach (var j in state.Specialists)
        {
            var job = new SpecialistJob(j.Id, j.Kind, j.Actor, j.Target, j.Point, j.Since, j.Until)
            { LastCell = j.LastCell, Pickup = j.Pickup, NextOrder = j.NextOrder, LastTarget = j.LastTarget, LastKind = j.LastKind,
                Stage = j.Stage, Probe = j.Probe, Done = j.Done };
            job.Passengers.AddRange(j.Passengers); _specialists.Add(job);
        }
    }

    private void ValidateState(World w, AiPlannerState s)
    {
        if (s.Identity is not { } id || s.Clock is not { } clock || s.Probes is not { } probes || s.Situation is null ||
            w.Player(Player) is null || id.Version != 4 || id.Player != Player || id.Tick != w.Tick || id.WorldHash != w.Hash() ||
            id.MatchDigest != w.Match.Digest || id.ContentHash != w.Content.Hash || id.PolicyDigest != PolicyDigest)
        { throw new ArgumentException("AI checkpoint does not match the player, world, policy or format."); }
        if (s.Structures is null || s.Intelligence is null || s.Objectives is null || s.Outcomes is null || s.Rest is null ||
            s.Pending is null || s.Missions is null || s.Cargo is null || s.Operations is null || s.Specialists is null ||
            probes.Cells is null || probes.Seen is null || probes.Retry is null) { throw new ArgumentException("Incomplete AI checkpoint."); }
        if (s.Structures.Any(x => x is null) || s.Intelligence.Any(x => x is null) || s.Objectives.Any(x => x is null) ||
            s.Outcomes.Any(x => x is null) || s.Rest.Any(x => x is null) || s.Pending.Any(x => x is null) ||
            s.Missions.Any(x => x is null) || s.Cargo.Any(x => x is null) || s.Operations.Any(x => x is null) || s.Specialists.Any(x => x is null))
        { throw new ArgumentException("Incomplete AI checkpoint entries."); }
        if ((int)s.Situation.Posture is < 0 or > 4 || s.Situation.Tick > w.Tick ||
            s.Situation.OwnStrength < 0 || s.Situation.KnownEnemyStrength < 0 || !w.Grid.Contains(s.Situation.Front))
        { throw new ArgumentException("Invalid saved strategic assessment."); }
        var p = _rules.Foundation;
        var count = ((w.Grid.Width + _rules.RegionCells - 1) / _rules.RegionCells) * ((w.Grid.Height + _rules.RegionCells - 1) / _rules.RegionCells);
        if (probes.Cells.Count != count || probes.Seen.Count != count || probes.Retry.Count != count || probes.Cells.Any(c => !w.Grid.Contains(c)) ||
            s.Missions.Count > _rules.MaxMissions || s.Intelligence.Count > (p?.MaxContacts ?? 0) || s.Objectives.Count > (p?.MaxObjectives ?? 0) ||
            s.Outcomes.Count > (p?.OutcomeCapacity ?? 0) || s.Operations.Count > (p?.MaxOperations ?? 0) ||
            s.Specialists.Count > (p?.SpecialistJobs ?? 0) || s.Pending.Count > 4096 || s.Rest.Count > 20_000 || s.Cargo.Count > 10_000 ||
            s.Structures.Count > 100_000 || clock.MissionCursor < 0 || clock.BuildCursor < 0 || !w.Grid.Contains(clock.Home))
        { throw new ArgumentException("AI checkpoint exceeds policy bounds or has invalid cells."); }
        SortedIds(s.Structures.Select(m => m.Id)); SortedIds(s.Intelligence.Select(c => c.Id));
        SortedIds(s.Missions.Select(m => m.Id)); SortedIds(s.Operations.Select(o => o.Id)); SortedIds(s.Specialists.Select(j => j.Id));
        if (s.BuildPlans is null || s.Sites is null || s.BuildPlans.Count > (PlanForState(w)?.Construction.Count ?? 0) || s.Sites.Count > 100_000 ||
            s.BuildPlans.Any(b => b is null) || s.Sites.Any(site => site is null) ||
            p?.BuildPlanning is null && (s.BuildPlans.Count != 0 || s.Sites.Count != 0))
        { throw new ArgumentException("Invalid construction planner state."); }
        SortedIds(s.BuildPlans.Select(b => (uint)(b.Goal + 1))); SortedIds(s.Sites.Select(site => site.Site));
        var side = 2 * _rules.BuildRadiusCells / _rules.BuildSpacingCells + 1;
        foreach (var b in s.BuildPlans)
        { if (b.Goal < 0 || b.Goal >= (PlanForState(w)?.Construction.Count ?? 0) || !w.Grid.Contains(b.Anchor) || b.Cursor < 0 ||
                b.Cursor >= side * side * 4 || b.FailedSweeps is < 0 or > 1_000_000) { throw new ArgumentException("Invalid placement search."); } }
        foreach (var site in s.Sites)
        { if (site.Work < 0 || site.ProgressAt > w.Tick) { throw new ArgumentException("Invalid construction progress."); } }
        var owners = new List<uint>();
        foreach (var m in s.Missions)
        {
            if (m is null || m.Members is null || m.Id > clock.MissionSerial || m.Members.Count > _rules.ForceSize ||
                (int)m.Kind is < 0 or > 3 || (int)m.Phase is < 0 or > 3 || m.Probe < -1 || m.Probe >= count ||
                !w.Grid.Contains(m.Objective) || m.RegroupPoint is { } rally && (!w.Grid.Contains(rally) || p?.Tactics is null ||
                    m.Kind != AiMissionKind.Assault || m.Phase != AiMissionPhase.Active) || !m.Done && m.Operation != 0 && !s.Operations.Any(o => o.Id == m.Operation))
            { throw new ArgumentException("Invalid saved mission."); }
            SortedIds(m.Members);
            if (!m.Done) { owners.AddRange(m.Members); }
        }
        foreach (var j in s.Specialists)
        {
            if (j is null || j.Passengers is null || j.Actor == 0 || j.Id > clock.JobSerial || (int)j.Kind is < 0 or > 9 ||
                j.Passengers.Count > (p?.TransportPassengers ?? 0) || j.Probe < -1 || j.Probe >= count || j.Stage is < 0 or > 3 ||
                !w.Grid.Contains(j.Point) || !w.Grid.Contains(j.Pickup)) { throw new ArgumentException("Invalid saved specialist."); }
            SortedIds(j.Passengers);
            if (!j.Done) { owners.Add(j.Actor); owners.AddRange(j.Passengers); }
        }
        owners.AddRange(s.Cargo.Where(j => !j.Done).Select(j => j.Truck));
        owners.Sort(); SortedIds(owners);
        foreach (var q in s.Pending)
        { if (q.Source == 0 || q.Def < -1 || q.Def >= w.Match.Defs.Count ||
            q.Kind is not (CommandKind.Produce or CommandKind.Build or CommandKind.Resume or CommandKind.Enter or CommandKind.UseAbility or CommandKind.CancelBuild)) { throw new ArgumentException("Invalid pending AI order."); } }
        foreach (var c in s.Intelligence)
        { if (c is null || (c.Kind != AiThreatKind.Air && !w.Grid.Contains(c.Cell)) || c.Seen > w.Tick || c.Strength < 0 ||
                c.Archetype < 0 || c.Archetype >= w.Content.Archetypes.Count || (int)c.Kind is < 0 or > 4 ||
                ((int)c.Roles & ~4095) != 0) { throw new ArgumentException("Invalid saved intelligence."); } }
        foreach (var o in s.Operations)
        { if (o.Id > clock.OperationSerial || o.Subject == 0 || (int)o.Phase is < 0 or > 2 || !w.Grid.Contains(o.Objective)) { throw new ArgumentException("Invalid saved operation."); } }
        foreach (var o in s.Objectives)
        { if ((o.Kind != AiThreatKind.Air && !w.Grid.Contains(o.Cell)) || o.Seen > w.Tick || (int)o.Kind is < 0 or > 4) { throw new ArgumentException("Invalid saved objective."); } }
        foreach (var o in s.Outcomes)
        { if (o.Tick > w.Tick || !w.Grid.Contains(o.Objective) || o.Reason is null) { throw new ArgumentException("Invalid saved outcome."); } }
        foreach (var c in s.Cargo)
        { if (c.Truck == 0 || c.Store == 0 || c.Kind is not (CommandKind.LoadCargo or CommandKind.Unload)) { throw new ArgumentException("Invalid saved cargo trip."); } }
    }

    private AiFactionPlan? PlanForState(World w) => _rules.Factions.FirstOrDefault(f => f.Faction == w.Match.Players[Player - 1].Faction.Id);

    private static void SortedIds(IEnumerable<uint> ids)
    {
        uint previous = 0;
        foreach (var id in ids)
        { if (id <= previous) { throw new ArgumentException("Checkpoint IDs must be positive, unique and sorted."); } previous = id; }
    }

    private ulong DigestPolicy()
    {
        var h = new PolicyHash();
        var r = _rules;
        foreach (var n in new[] { r.Version, r.DecisionTicks, r.CommandsPerDecision, r.MaxMissions, r.MissionsPerDecision, r.ForceSize,
            r.ForceMinimum, r.AssemblyTicks, r.MissionTicks, r.StallTicks, r.RecoveryTicks, r.RestTicks, r.HomeGuards, r.ScoutMissions,
            r.DefendCells, r.ArrivalCells, r.ProgressCells, r.ReadyHpPercent, r.RetreatHpPercent, r.RetreatMembersPercent,
            r.RegionCells, r.RescoutTicks, r.ProbeRetryTicks, r.ReachSearchCells, r.AssaultCells, r.MarchCells, r.FactoryQueue,
            r.BuildRetryTicks, r.BuildRadiusCells, r.BuildSpacingCells, r.BuildChecksPerDecision, r.ArmyCap, r.SupplyLowPercent, r.SupplyTripCells }) { h.Add(n); }
        h.Add(r.FormSquads ? 1 : 0); h.Add(r.Factions.Count);
        foreach (var f in r.Factions)
        {
            h.Text(f.Faction);
            foreach (var goals in new[] { f.Production, f.Construction })
            {
                h.Add(goals.Count);
                foreach (var g in goals) { h.Text(g.Unit); h.Add(g.Count); h.Add((int)g.Against); h.Add(g.ExtraPerThreat); h.Add(g.Maximum); h.Add(g.AfterTick); h.Add(g.Forward ? 1 : 0); }
            }
        }
        h.Add(r.Foundation is null ? 0 : 1);
        if (r.Foundation is { } p)
        {
            foreach (var n in new[] { p.StrategyTicks, p.ContactTicks, p.MaxContacts, p.MaxObjectives, p.ThreatCells, p.DefendStrengthPercent,
                p.AttackStrengthPercent, p.DistanceWeight, p.DangerWeight, p.StructureValue, p.DefenseValue, p.AssignmentPenalty,
                p.MaxOperations, p.OperationForces, p.OperationGatherTicks, p.OperationTicks, p.StageCells, p.FlankCells,
                p.SpecialistJobs, p.SpecialistTicks, p.SpecialistOrderTicks, p.AirFuelPercent, p.AirAmmoPercent, p.AirAreaCells,
                p.ArtilleryCells, p.ServiceHpPercent, p.ExpansionCells, p.BoardingTicks, p.TransportPassengers,
                p.OutcomeCapacity, p.FailurePenalty, p.OutcomeTicks, p.ApproachChecks }) { h.Add(n); }
            h.Add(p.BuildPlanning is null ? 0 : 1);
            if (p.BuildPlanning is { } build)
            {
                foreach (var n in new[] { build.GoalsPerDecision, build.CandidatesPerGoal, build.SliceRetryTicks, build.FailedSweepTicks,
                    build.SiteStallTicks, build.SiteRetryTicks, build.AbandonTicks, build.MaxSites, build.BuilderReserve, build.MissingProducerBonus }) { h.Add(n); }
                h.Add(build.CancelAbandoned ? 1 : 0);
            }
            h.Add(p.Tactics is null ? 0 : 1);
            if (p.Tactics is { } tactics)
            {
                foreach (var n in new[] { tactics.TargetCandidates, tactics.EngageCells, tactics.EngagePercent, tactics.OrderTicks,
                    tactics.WeaponWeight, tactics.ReturnFireBonus, tactics.ObjectiveBonus, tactics.FocusBonus, tactics.FinishWeight,
                    tactics.DistanceWeight, tactics.RegroupCells, tactics.RegroupTicks, tactics.RegroupRetryTicks, tactics.RallyCandidates }) { h.Add(n); }
            }
            h.Add(p.PostureHoldTicks); h.Add(p.Postures.Count);
            foreach (var profile in p.Postures)
            {
                foreach (var n in new[] { (int)profile.Posture, profile.DefenderPercent, profile.AssaultForces, profile.Scouts,
                    profile.CombatPriority, profile.CounterPriority, profile.InfrastructurePriority, profile.LogisticsPriority,
                    profile.ReconPriority, profile.DefensePriority }) { h.Add(n); }
                h.Add(profile.DistantStrikes ? 1 : 0); h.Add(profile.Insertions ? 1 : 0); h.Add(profile.ForwardBuild ? 1 : 0);
            }
            h.Add(p.Aircraft ? 1 : 0); h.Add(p.Transports ? 1 : 0); h.Add(p.Expansion ? 1 : 0);
            h.Add(p.Composition.Count);
            foreach (var c in p.Composition) { h.Add((int)c.Role); h.Add(c.Minimum); h.Add(c.Essential ? 1 : 0); }
            h.Add(p.Roles.Count);
            foreach (var c in p.Roles) { h.Add((int)c.Role); h.Add(c.AnyTags.Count); foreach (var tag in c.AnyTags) { h.Text(tag); } }
        }
        return h.Value;
    }

    private struct PolicyHash()
    {
        public ulong Value = 14695981039346656037;
        public void Add(long value)
        { for (var i = 0; i < 8; i++) { Value = unchecked((Value ^ (byte)((ulong)value >> (8 * i))) * 1099511628211); } }
        public void Text(string text) { Add(text.Length); foreach (var c in text) { Add(c); } }
    }
}
