using SimCore.FixedPoint;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Checkpoint compatibility identity. The host must restore the matching world and its pending commands separately.</summary>
public sealed record AiStateIdentity(int Version, byte Player, uint Tick, ulong WorldHash, ulong MatchDigest, string ContentHash, ulong PolicyDigest);

/// <summary>Planner clocks, monotonic IDs, cursors and home.</summary>
public sealed record AiStateClock(uint NextDecision, uint NextStrategy, uint NextBuild, uint MissionSerial, uint OperationSerial,
    uint JobSerial, int MissionCursor, int BuildCursor, Cell Home, uint PostureUntil = 0, uint NextReinforcement = 0, int ReinforcementReadyCount = 0);

/// <summary>Exploration memory including adjusted reachable probe cells.</summary>
public sealed record AiProbeMemory(IReadOnlyList<Cell> Cells, IReadOnlyList<uint> Seen, IReadOnlyList<uint> Retry);

/// <summary>A remembered structure, without a live object reference.</summary>
public sealed record AiStructureMemory(uint Id, Cell Cell);

/// <summary>A unit's reassignment cooldown.</summary>
public sealed record AiRestMemory(uint Unit, uint Until);

/// <summary>A host command reservation that has not reached its execution tick.</summary>
public sealed record AiPendingMemory(uint Source, int Def, CommandKind Kind, uint Due, uint Target);

/// <summary>Complete private state of a combat mission.</summary>
public sealed record AiMissionMemory(uint Id, AiMissionKind Kind, AiMissionPhase Phase, uint Started, uint Deadline,
    uint ProgressAt, uint NextOrder, long BestDistance, Cell Objective, Cell LastCell, CommandKind LastKind,
    uint LastTarget, uint Contact, uint Operation, int Approach, Cell? FinalObjective, int Probe, int LaunchMembers,
    bool Done, bool OutcomeRecorded, IReadOnlyList<uint> Members, Cell? RegroupPoint = null, uint RegroupUntil = 0, uint NextRegroup = 0);

/// <summary>Complete private state of a cargo reservation.</summary>
public sealed record AiCargoMemory(uint Truck, uint Store, CommandKind Kind, uint Until, uint NextOrder, bool Done);

/// <summary>Complete private state of an operation.</summary>
public sealed record AiOperationMemory(uint Id, uint Subject, Cell Objective, uint ReleaseAt, uint Deadline, AiOperationPhase Phase);

/// <summary>Complete private state of a specialist executor.</summary>
public sealed record AiSpecialistMemory(uint Id, AiSpecialistKind Kind, uint Actor, uint Target, Cell Point, Cell LastCell,
    Cell Pickup, uint Since, uint Until, uint NextOrder, uint LastTarget, CommandKind LastKind, int Stage, int Probe,
    bool Done, IReadOnlyList<uint> Passengers);

/// <summary>
/// Versioned host planner checkpoint, containing no live Unit references. Diagnostics are deliberately excluded.
/// Restore it only with a matching world checkpoint/replayed world and the same pending input commands.
/// </summary>
public sealed record AiPlannerState(AiStateIdentity Identity, AiStateClock Clock, AiProbeMemory Probes,
    IReadOnlyList<AiStructureMemory> Structures, IReadOnlyList<AiContact> Intelligence, AiSituation Situation,
    IReadOnlyList<AiObjective> Objectives, IReadOnlyList<AiMissionOutcome> Outcomes, IReadOnlyList<AiRestMemory> Rest,
    IReadOnlyList<AiPendingMemory> Pending, IReadOnlyList<AiMissionMemory> Missions, IReadOnlyList<AiCargoMemory> Cargo,
    IReadOnlyList<AiOperationMemory> Operations, IReadOnlyList<AiSpecialistMemory> Specialists,
    IReadOnlyList<AiBuildPlanMemory>? BuildPlans = null, IReadOnlyList<AiSiteMemory>? Sites = null, IReadOnlyList<AiCrewMemory>? Crews = null);
