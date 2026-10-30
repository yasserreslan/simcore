using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Current strategic intent, derived only from friendly state and observed intelligence.</summary>
public enum AiPosture
{
    /// <summary>Reestablish production or recover depleted forces.</summary>
    Rebuild,
    /// <summary>Counter a threat to owned structures.</summary>
    Defend,
    /// <summary>Find an enemy or refresh stale intelligence.</summary>
    Recon,
    /// <summary>Establish a forward position while keeping forces intact.</summary>
    Expand,
    /// <summary>Commit forces to known objectives.</summary>
    Attack,
}

/// <summary>A strategic assessment snapshot. Strengths are heuristic scores, not simulated combat predictions.</summary>
public sealed record AiSituation(uint Tick, AiPosture Posture, long OwnStrength, long KnownEnemyStrength,
    int GroundThreats, int AirThreats, int ArmorThreats, int KnownStructures, Cell Front);

/// <summary>A ranked observation-derived objective. Subject is zero for exploration.</summary>
public sealed record AiObjective(uint Subject, Cell Cell, AiThreatKind Kind, long Utility, uint Seen, bool Visible);

/// <summary>A frozen observation; never a reference to a live hidden enemy.</summary>
public sealed record AiContact(uint Id, byte Owner, int Archetype, Cell Cell, uint Seen, AiRole Roles,
    AiThreatKind Kind, int Strength, int Value, bool Structure);

/// <summary>A completed mission result used for bounded, expiring objective penalties.</summary>
public sealed record AiMissionOutcome(uint Mission, Cell Objective, uint Tick, bool Success, string Reason);

public sealed partial class PlayerAi
{
    private readonly List<AiContact> _intel = [];
    private readonly List<AiObjective> _objectives = [];
    private readonly List<AiMissionOutcome> _outcomes = [];
    private uint _nextStrategy;
    private AiSituation _situation = new(0, AiPosture.Recon, 0, 0, 0, 0, 0, 0, default);

    /// <summary>The most recent strategic assessment.</summary>
    public AiSituation Situation => _situation;
    /// <summary>Frozen enemy observations, in ID order.</summary>
    public IReadOnlyList<AiContact> Intelligence => _intel.AsReadOnly();
    /// <summary>Ranked strategic objectives, best first.</summary>
    public IReadOnlyList<AiObjective> Objectives => _objectives.AsReadOnly();
    /// <summary>Bounded mission outcomes, oldest first.</summary>
    public IReadOnlyList<AiMissionOutcome> Outcomes => _outcomes.AsReadOnly();

    /// <summary>Classifies a compiled definition by capabilities and optional content-tag overrides.</summary>
    public AiRole RolesOf(UnitDef def)
    {
        var role = AiRole.None;
        var air = def.Archetype.Flight is not null;
        if (def.Speed.Raw > 0 && !def.IsStructure) { role |= AiRole.Scout; }
        if (def.Builder is not null) { role |= AiRole.Engineer; }
        if (def.Cargo is not null) { role |= AiRole.Logistics; }
        if (def.Repairs.Count > 0) { role |= AiRole.Repair; }
        if (def.Archetype.AirVision.Raw > 0) { role |= AiRole.Radar; }
        if (def.Crew is { Passengers: > 0 } || def.Archetype.Flight?.Rotor?.Riders is not null) { role |= AiRole.Transport; }
        foreach (var weapon in def.Weapons)
        {
            if ((weapon.Effect.Targets & Layers.Ground) != 0 && weapon.Effect.Interceptor is null)
            {
                role |= air ? AiRole.Strike : AiRole.Frontline;
                if (weapon.Effect.Guided is not null) { role |= AiRole.AntiArmor; }
                if (!air && (def.Archetype.DeployTicks > 0 || weapon.Range.Raw >= (long)(_rules.Foundation?.ArtilleryCells ?? _rules.AssaultCells) * Fixed.OneRaw))
                { role |= AiRole.Artillery; }
            }
            if ((weapon.Effect.Targets & Layers.Air) != 0 || weapon.Effect.Interceptor is not null)
            { role |= air ? AiRole.Fighter : AiRole.AntiAir; }
        }
        if (_rules.Foundation is { } policy && _world is { } w)
        {
            foreach (var r in policy.Roles)
            {
                if (r.AnyTags.Any(tag => w.Content.Tags.Contains(tag) && def.Tags.Matches(w.Content.TagsOf(tag)))) { role |= r.Role; }
            }
        }
        return role;
    }

    private static int Strength(Unit u)
    {
        long fire = 0;
        foreach (var weapon in u.Def.Weapons)
        {
            if (weapon.Stock >= 0 && u.Charges[weapon.Stock] <= 0) { continue; }
            fire += (long)Math.Max(0, weapon.Damage) * Math.Max(1, weapon.Effect.SalvoSize) * World.TicksPerSecond / Math.Max(1, weapon.Reload);
        }
        return (int)Math.Min(int.MaxValue, fire * Math.Max(0, u.HP) / Math.Max(1, u.Def.MaxHp));
    }

    private void RememberContacts(World w)
    {
        if (_rules.Foundation is not { } policy) { return; }
        _intel.RemoveAll(c => c.Structure ? !_memory.Any(m => m.Id == c.Id) : w.Tick - c.Seen > policy.ContactTicks);
        foreach (var e in _visible)
        {
            var kind = e.Def.IsStructure ? AiThreatKind.Structures : e.Def.Layer == Layers.Air ? AiThreatKind.Air :
                e.Def.Crew is not null ? AiThreatKind.Armor : AiThreatKind.Ground;
            var contact = new AiContact(e.Id, e.Owner, e.Def.Archetype.Index, Cell.Of(e.Pos), w.Tick, RolesOf(e.Def), kind,
                Strength(e), Math.Max(1, e.Def.Archetype.Cost), e.Def.IsStructure);
            var at = _intel.FindIndex(c => c.Id >= e.Id);
            if (at < 0) { _intel.Add(contact); }
            else if (_intel[at].Id == e.Id) { _intel[at] = contact; }
            else { _intel.Insert(at, contact); }
        }
        while (_intel.Count > policy.MaxContacts)
        {
            var oldest = 0;
            for (var i = 1; i < _intel.Count; i++)
            {
                if ((!_intel[i].Structure && _intel[oldest].Structure) || (_intel[i].Structure == _intel[oldest].Structure &&
                    (_intel[i].Seen < _intel[oldest].Seen || (_intel[i].Seen == _intel[oldest].Seen && _intel[i].Id > _intel[oldest].Id)))) { oldest = i; }
            }
            _intel.RemoveAt(oldest);
        }
    }

    private void AssessStrategy(World w)
    {
        if (_rules.Foundation is not { } p || w.Tick < _nextStrategy) { return; }
        _nextStrategy = w.Tick + (uint)p.StrategyTicks;
        _outcomes.RemoveAll(o => w.Tick - o.Tick > p.OutcomeTicks);
        long own = 0, enemy = 0, homeThreat = 0;
        var ground = 0; var air = 0; var armor = 0; var structures = 0;
        foreach (var u in _own) { if (u.Inside == 0 && u.IsBuilt) { own += Strength(u); } }
        foreach (var c in _intel)
        {
            var strength = ContactStrength(w, c);
            enemy += strength;
            if (Near(Home, c.Cell, _rules.DefendCells) && _visible.Any(e => e.Id == c.Id)) { homeThreat += strength; }
            switch (c.Kind)
            {
                case AiThreatKind.Air: air++; break;
                case AiThreatKind.Armor: armor++; ground++; break;
                case AiThreatKind.Ground: ground++; break;
                case AiThreatKind.Structures: structures++; break;
            }
        }
        var posture = homeThreat * 100 >= Math.Max(1, own) * p.DefendStrengthPercent ? AiPosture.Defend :
            !_own.Any(u => u.IsBuilt && u.Def.Archetype.Production is not null) && _own.Any(u => u.Def.Builder is not null) ? AiPosture.Rebuild :
            _intel.Count == 0 ? AiPosture.Recon : own * 100 >= Math.Max(1, enemy) * p.AttackStrengthPercent ? AiPosture.Attack :
            p.Expansion && structures > 0 ? AiPosture.Expand : AiPosture.Recon;
        var front = _intel.Where(c => c.Structure).OrderBy(c => Distance(Home, c.Cell)).ThenBy(c => c.Id).FirstOrDefault()?.Cell ?? Home;
        if (posture != _situation.Posture) { Trace(w, 0, "posture-" + posture); }
        _situation = new AiSituation(w.Tick, posture, own, enemy, ground, air, armor, structures, front);
        _objectives.Clear();
        foreach (var c in _intel)
        {
            var visible = _visible.Any(e => e.Id == c.Id);
            var utility = (long)c.Value + (c.Structure ? p.StructureValue : 0) -
                Fixed.Sqrt(Distance(Home, c.Cell)) / Fixed.OneRaw * p.DistanceWeight - Danger(w, c.Cell, false) * p.DangerWeight;
            if (visible && Near(Home, c.Cell, _rules.DefendCells)) { utility += p.DefenseValue; }
            utility -= _missions.Count(m => !m.Done && m.Contact == c.Id) * (long)p.AssignmentPenalty;
            utility -= _outcomes.Count(o => !o.Success && Near(o.Objective, c.Cell, _rules.ArrivalCells)) * (long)p.FailurePenalty;
            AddObjective(new AiObjective(c.Id, c.Cell, c.Kind, utility, c.Seen, visible), p.MaxObjectives);
        }
        for (var i = 0; i < _probes.Length; i++)
        {
            if (!ProbeAvailable(w, i)) { continue; }
            var utility = -Fixed.Sqrt(Distance(Home, _probes[i])) / Fixed.OneRaw * p.DistanceWeight - Danger(w, _probes[i], false) * p.DangerWeight;
            AddObjective(new AiObjective(0, _probes[i], AiThreatKind.None, utility, _seen[i], false), p.MaxObjectives);
        }
    }

    private void AddObjective(AiObjective objective, int capacity)
    {
        var at = _objectives.FindIndex(o => objective.Utility > o.Utility || (objective.Utility == o.Utility &&
            (objective.Subject < o.Subject || (objective.Subject == o.Subject && (objective.Cell.Y < o.Cell.Y ||
                (objective.Cell.Y == o.Cell.Y && objective.Cell.X < o.Cell.X))))));
        if (at < 0) { if (_objectives.Count < capacity) { _objectives.Add(objective); } return; }
        _objectives.Insert(at, objective);
        if (_objectives.Count > capacity) { _objectives.RemoveAt(capacity); }
    }

    private long ContactStrength(World w, AiContact c) => c.Structure || _rules.Foundation is not { } p ? c.Strength :
        (long)c.Strength * Math.Max(0, p.ContactTicks - (long)(w.Tick - c.Seen)) / p.ContactTicks;

    private long Danger(World w, Cell at, bool air)
    {
        if (_rules.Foundation is not { } p) { return 0; }
        long value = 0;
        foreach (var c in _intel)
        {
            if (air && (c.Roles & (AiRole.AntiAir | AiRole.Fighter)) == 0) { continue; }
            var distance = Fixed.Sqrt(Distance(at, c.Cell)) / Fixed.OneRaw;
            if (distance >= p.ThreatCells) { continue; }
            value += ContactStrength(w, c) * (p.ThreatCells - distance) / p.ThreatCells;
        }
        return value;
    }

    private int Desired(AiUnitGoal goal, World w)
    {
        if (w.Tick < goal.AfterTick) { return 0; }
        if (_rules.Foundation is null || goal.Against == AiThreatKind.None) { return goal.Count; }
        var count = goal.Against switch
        { AiThreatKind.Air => _situation.AirThreats, AiThreatKind.Armor => _situation.ArmorThreats,
          AiThreatKind.Structures => _situation.KnownStructures, _ => _situation.GroundThreats };
        return (int)Math.Min(goal.Maximum > 0 ? goal.Maximum : goal.Count, (long)goal.Count + (long)count * goal.ExtraPerThreat);
    }

    private bool CompositionReady(Mission m, bool essentialOnly)
    {
        if (_rules.Foundation is not { } p) { return true; }
        return p.Composition.Where(r => !essentialOnly || r.Essential).All(r =>
            m.Members.Count(id => Own(id) is { } u && (RolesOf(u.Def) & r.Role) != 0) >= r.Minimum);
    }

    private void RecordOutcome(World w, Mission m, string reason, bool success)
    {
        if (_rules.Foundation is not { } p || m.OutcomeRecorded) { return; }
        m.OutcomeRecorded = true;
        if (_outcomes.Count == p.OutcomeCapacity) { _outcomes.RemoveAt(0); }
        _outcomes.Add(new AiMissionOutcome(m.Id, m.FinalObjective ?? m.Objective, w.Tick, success, reason));
    }
}
