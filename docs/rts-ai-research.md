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
