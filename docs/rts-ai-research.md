# Open-source RTS AI: research used for PlayerAi v1

Source review on 30 October 2026. These are architectural observations from the linked code, not comparative playtesting. The implementation in this repository is original C# built on SimCore commands; no source from these projects was copied into the engine.

## OpenRA: modular decisions and normal player orders

Reviewed commit **9636b63eaee62fcb60ef2e896e7985bdf2f1ee68**.

[ModularBot](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/Player/ModularBot.cs) separates host-side bot decisions from simulation mutations. Modules enqueue orders; the bot drains orders over ticks. It explicitly avoids activating bot logic during replay playback.

[SquadManagerBotModule](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/BotModules/SquadManagerBotModule.cs) keeps active-unit membership, periodically assigns roles, spreads squad updates over ticks, and throttles responses to attacks. [UnitBuilderBotModule](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/BotModules/UnitBuilderBotModule.cs) handles explicit production requests separately from composition-driven production, checks resources, and rotates production categories.

**Applied:** PlayerAi is an external command generator, with separate mission, economy, and support modules. Commands, missions, queue depth, and placement checks have policy limits. Combat mission updates rotate in mission-ID order. Existing replay playback runs no new AI.

**Not copied:** its use of local randomness, hash collections, or actor-trait architecture. SimCore keeps integer comparisons and stable ID/index ordering. The foundation prioritizes normalized desired-count deficits and deterministic role quotas; it does not implement a general composition optimizer.

## 0 A.D. / Petra: persistent attack plans and readiness

Reviewed commit **61a3b9507d974084e6badb88a0826bd89a6d5b8b**.

[AttackManager](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/binaries/data/mods/public/simulation/ai/petra/attackManager.js) maintains upcoming and started attack plans and handles preparation, launch, abort, and reuse. [AttackPlan](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/binaries/data/mods/public/simulation/ai/petra/attackPlan.js) distinguishes minimum from target force sizes, assigns members, uses rally points, and considers accessibility and transport when choosing targets and routes.

[QueueManager](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/binaries/data/mods/public/simulation/ai/petra/queueManager.js) manages resource accounts and priorities for multiple queues. Its own comments explain a weakness of rigid reservations: partially funded queues can hold resources that another queue could use immediately.

**Applied:** each PlayerAi force has an objective, exclusive members, assembly deadline, progress deadline, and recovery phase. It checks movement-class connectivity before selecting objectives. A small force can launch at its deadline rather than wait indefinitely. Completed or failed missions release members with a rest period.

**Foundation extension:** role-specific essential/optional composition, multi-force staging and reserved transport executors now have independent code. Resource-account allocation remains deferred. SimCore's construction/production Charge methods currently do not spend credits; implementing imaginary resource availability in the AI would misrepresent the engine. Production is instead bounded by explicit count goals, queue capacity, and an army cap that includes produced drivers.

## Warzone 2100 / NullBot: policy data, target persistence, and recovery

Reviewed commit **559c67c6c472361b5566332b57351781155e16c9**.

[nb_generic.js](https://github.com/Warzone2100/warzone2100/blob/559c67c6c472361b5566332b57351781155e16c9/data/mp/multiplay/skirmish/nb_generic.js) defines personalities using weapon paths, research choices, desired force sizes, and repair thresholds. It loads common behavior rather than defining a different engine for every personality.

[tactics.js](https://github.com/Warzone2100/warzone2100/blob/559c67c6c472361b5566332b57351781155e16c9/data/mp/multiplay/skirmish/nb_includes/tactics.js) retains group targets, distinguishes harassment suitability, regroups forces, checks reachability, and sends damaged units toward repair behavior.

**Applied:** policy lives in strict JSON, separate from simulation content. Forces retain objectives and retreat on aggregate health, member losses, empty ground weapons, mission expiration, or lack of progress. Cargo has reserved destinations and expiration/cancellation. Units seek ammunition at reachable stocked friendly suppliers.

**Foundation extension:** observed threat counts now alter capped production goals; expiring failed-objective penalties affect ranking. Research trees, allied strategy sharing and a learned combat-power model remain deferred.

## Generals / Zero Hour: workflow boundaries

The earlier source study of [EA's release](https://github.com/electronicarts/CnC_Generals_Zero_Hour/tree/0a05454d8574207440a5fb15241b98ad0b435590) also informed the implementation: keep player planning above unit execution, resume unfinished construction, and put deadlines on assembly and specialist jobs. Its external authored skirmish scripts are absent from that repository; exact shipped faction strategies were not reconstructed.

PlayerAi preserves SimCore's stricter information model: enemy details enter the planner only through current visibility; remembered structures disappear only when their old cell is observed empty. It does not adopt enemy-global lookups from the Generals implementation.

## What the player foundation delivers

The initial ground player now has additional executable strategy, operation, specialist, transport,
ability and state modules. It classifies capabilities, retains frozen observations, ranks objectives,
recruits role quotas, stages multiple forces, plans aircraft jobs and passenger insertion, services units,
escorts cargo, adapts production and serializes planner checkpoints. See [the implementation rules](player-ai.md)
for exact lifecycles, information limits, policy fields and remaining work.

These are independent implementations informed by the patterns above, not imported reference-game
code. Existing world systems and old bots remain separate. Unsupported infantry training, a working
cash economy, research, alliance plans and richer tactical planning remain outside this change.
The expansion was compile-checked only at the owner's request; earlier ground fixtures do not
establish expanded behavior. Win rate, large-map performance and exact checkpoint continuation
have not been demonstrated.

## Construction recovery follow-up (2026-10-30)

Further review stayed on pinned primary sources. Petra's
[BuildManager](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/binaries/data/mods/public/simulation/ai/petra/buildManager.js)
tracks available builders and temporarily unbuildable templates, including reason/expiry and serialized memory.
The useful architectural lesson is to remember a failed attempt without letting it consume every later decision.

OpenRA's [BaseBuilderBotModule](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/BotModules/BaseBuilderBotModule.cs)
exposes retry thresholds. Its [BaseBuilderQueueManager](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/BotModules/BotModuleLogic/BaseBuilderQueueManager.cs)
backs off after failed placement, considers changes in available base providers/buildings, and prioritizes production/economy needs.
Its resource accounting and randomized choices do not transfer directly to SimCore.

**Implemented independently:** `PlayerAiConstruction` gives policy goals separate center-out placement cursors,
short slice delays and full-sweep backoff under one shared candidate budget. Friendly work-progress clocks
identify stalled sites; retries prefer an available replacement worker, use footprint-edge access and preserve
in-flight reservations. A policy can cancel long-stalled configured sites using normal CancelBuild. Builder
reservation and a missing-producer priority bonus connect recovery to the existing economy executor. Format 4
checkpoints retain placement and progress clocks. No reference source code was imported.

**Limits:** this is scheduling and recovery, not a cash economy or an expansion optimizer. It does not invent
missing workers/producers, prove route safety, or guarantee successful placement. Compilation is the only local
validation of this increment; fixtures, representative matches and timing measurements remain outstanding.

## Standard-opponent force loop (M1)

The pinned [Petra AttackPlan](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/binaries/data/mods/public/simulation/ai/petra/attackPlan.js)
uses persistent plans with rally points and per-category minimum/target force sizes. The pinned
[OpenRA SquadManager](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/BotModules/SquadManagerBotModule.cs)
tracks active units and has separate role-assignment and force-update intervals. These reinforce a useful
architecture: unit allocation is ongoing work, not merely creating an initial attack wave.

The independent M1 increment adds bounded local reinforcement, whole-squad ownership transfers from
surplus defense and an optional attack reserve floor. It also makes unarmed reconnaissance and crewing
lifecycles explicit. This is not a port of either allocator, and no reference source was copied. No new
combat-quality or performance claim follows from the source review. See [the milestone](ai-milestone.md)
for concrete expectations, content dependencies and deferred validation.

## Faction identity, tunnels and air operations

The additional source review focuses on concrete lifecycles rather than faction names alone:

- [EA Generals AI interface](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/Generals/Code/GameEngine/Include/GameLogic/AI.h)
  exposes `aiGuardTunnelNetwork`. It establishes a tunnel-specific AI interface; it does not reveal a
  complete shipped GLA skirmish script. SimCore should exploit its own network rules rather than claim
  to recreate Generals' exact tunnel behavior.
- [0 A.D. Petra transport plan](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/binaries/data/mods/public/simulation/ai/petra/transportPlan.js)
  makes boarding, transport ownership, capacity and landing/retry stages explicit. The architectural
  lesson is to retain the mission through containment and failed arrivals. SimCore applies that idea
  independently to tunnel boarding/transit/evacuation with its own capacity, size and collapse rules.
- [Warzone NullBot tactics](https://github.com/Warzone2100/warzone2100/blob/559c67c6c472361b5566332b57351781155e16c9/data/mp/multiplay/skirmish/nb_includes/tactics.js)
  selects VTOL targets using weapon suitability and priorities, and retains group targets with regroup/
  repair decisions. This supports bounded role-specific jobs and compatible observed targets. Its target
  logic is not proof of a complete refueling planner. Warzone's official
  [quickstart aircraft guidance](https://github.com/Warzone2100/warzone2100/blob/master/doc/quickstartguide.asciidoc)
  also describes finite ammunition and rearming as ordinary aircraft constraints.

The resulting independent implementation is documented in [faction doctrines](ai-faction-doctrines.md).
Resistance combines light forces, observation, logistics targets and useful tunnel travel; Sentinel
combines armor, radar, air infrastructure, guarded sorties and servicing. Both share the same legality,
fog and allocation foundation and retain their own weighted battle styles. No reference-game source was
copied. Successful matches, tactical quality and search costs remain deferred validation work.

## Local judgment, pressure sharing and recovery

A further focused review informs the shared tactical layer:

- [OpenRA AttackOrFleeFuzzy](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/BotModules/Squads/AttackOrFleeFuzzy.cs)
  combines health, relative attack power and speed to decide whether a squad can attack. The useful
  lesson is local comparison rather than relying only on a global strategic ratio. SimCore independently
  uses armor-adjusted integer weapon scores, discounted nearby support, complete-sample checks and
  confirmation/grace clocks. It does not port the fuzzy rules or runtime.
- [Petra DefenseArmy](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/binaries/data/mods/public/simulation/ai/petra/defenseArmy.js)
  tracks defender assignments against opponents and skips some saturated targets while retaining a
  fallback. It also releases armies toward suitable friendly positions. SimCore applies a soft
  cross-mission commitment penalty and scores reachable recovery providers with known danger.
- [NullBot tactics](https://github.com/Warzone2100/warzone2100/blob/559c67c6c472361b5566332b57351781155e16c9/data/mp/multiplay/skirmish/nb_includes/tactics.js)
  separates repaired members, cluster regrouping and recent-danger response. This reinforces the need
  for a recover-and-return lifecycle rather than repeatedly issuing attacks to depleted units.

These are architectural observations, not claims that the source algorithms have identical visibility
or determinism requirements. The [implemented mechanics](ai-combat-control.md) are independently written,
with count/tick limits, normal commands and explicit approximation/validation limits. No reference code
or external fuzzy/AI dependency is imported.

## Reconnaissance and preservation

The pinned [OpenRA ground states](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/BotModules/Squads/States/GroundStates.cs)
separate movement, attacking and fleeing, reconsider commitment during movement, and return stalled
movement to an idle decision. The lesson for independent scouts is to reconsider a dangerous trip before
health depletion forces the decision. This source is general ground-squad logic, not a dedicated scout
planner; it does not establish the exact exploration algorithm used here.

SimCore's independent [reconnaissance layer](ai-reconnaissance.md) estimates information from stale/unseen
neighboring regions, values refreshing frozen contacts, and penalizes travel/known danger. Visible stocked
weapon ranges trigger scout recovery. Candidate/route limits and existing probe-retry memory keep failed
or dangerous goals from dominating repeated choices. This is a coarse utility heuristic, not a port of
OpenRA movement or a claim of exact frontier/visibility optimization. Behavioral and performance evidence
remain deferred under the owner's no-local-testing instruction.

## Preparation and shared commitment

The pinned [Petra AttackPlan](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/binaries/data/mods/public/simulation/ai/petra/attackPlan.js)
has a completing phase for regrouping and returning resources, checks transport completion, and bounds
some forced-order waits. It distinguishes preparation failure, continued preparation and starting the
attack. The useful architectural lesson is a readiness decision with explicit failure/timeout behavior,
rather than interpreting a timer firing as proof that an army is ready.

The pinned [NullBot tactics](https://github.com/Warzone2100/warzone2100/blob/559c67c6c472361b5566332b57351781155e16c9/data/mp/multiplay/skirmish/nb_includes/tactics.js)
uses the largest repaired cluster when regrouping and treats an insufficient cluster differently when
recently threatened. This supports separating available force from raw ownership count.

SimCore's independent [operation controller](ai-operation-control.md) checks healthy armed staging,
concentrates additional whole forces within caps, permits bounded preparation extension, and tracks
shared progress/losses after release. Known-danger samples and staging separation are utility heuristics,
not source-game path algorithms. Frozen-target updates, tunnel ownership and exact checkpoint clocks
remain SimCore-specific. No source code was imported, and no combat-quality claim follows from compiling.
