# Player AI foundation

`Bots/PlayerAi` is an opt-in, host-owned opponent planner. The concrete standard-opponent target is [M1: Sustainable Standard Opponent](ai-milestone.md). It emits ordinary delayed commands; the engine remains responsible for movement, combat, visibility, production, boarding and servicing. Existing `Bot` and `SkirmishAi` behavior stays separate. The reference study is in [rts-ai-research.md](rts-ai-research.md); this implementation does not import reference-game code.

## Host integration

```csharp
var policy = PlayerAiLoader.Load("content/ai/player.json");
var bot = new PlayerAi(playerId, policy);
foreach (var command in bot.Think(world))
    RecordAndBroadcast(world.Issue(command));
world.Step();
```

Use one instance per controlled player and world on the authoritative command-generating host. `Think` neither issues commands nor changes units. Repeated calls within a tick return no extra commands. Playback uses recorded commands without running the planner. Rules are copied on construction; exported mission, operation and specialist views contain detached values.

```sh
dotnet run --project src/SimRun -- -player-ai content/ai/player.json -setup content/setups/player_ai.json -fog true -ticks 2400 -save /tmp/ai-replay.json -ai-trace /tmp/ai-trace.json -ai-state /tmp/ai-state.json
dotnet run --project src/SimRun -- -player-ai content/ai/player.json -replay /tmp/ai-replay.json -ai-resume /tmp/ai-state.json -ticks 4800 -save /tmp/ai-resumed.json
```

Resume's `-ticks` is the absolute final tick. The demo lobby adds workers for construction. The runner controls all lobby players and currently accepts flat setups; hosts may supply terrain directly through the public API. Prepare navigation caches while loading terrain as usual. A checkpoint export requires at least one `Think` call to bind each planner.

## Executable layers

| File | Responsibility |
| --- | --- |
| PlayerAi | Observation boundary, decision schedule, command budget, exclusive orders and reservations |
| PlayerAiStrategy | Capability roles, frozen contacts, strength/danger estimates, posture, objective ranking and recent outcomes |
| PlayerAiMissions | Scout/Assault/Defend/Recover forces, composition, squads, progress and retreat |
| PlayerAiForces / Crews | Squad-safe reserve redistribution, local reinforcement, observers and bounded driver jobs |
| PlayerAiCapabilities | Diagnostic content/roster blockers; no effect on decisions |
| PlayerAiTactics | Visible weapon-compatible focus targets, bounded cohesion rallies and retry clocks |
| PlayerAiOperations | Multi-force staging, alternative approaches, synchronized release and withdrawal |
| PlayerAiSpecialists | Aircraft, repair/service, artillery, radar/air defense and cargo escorts |
| PlayerAiTransport | Passenger reservations, pickup, boarding, insertion, unload and cancellation |
| PlayerAiAbilities | Charged launch abilities for observation aircraft or visible-target strike bodies |
| PlayerAiConstruction | Independent placement searches, site progress/retry, builder protection and missing-producer demand |
| PlayerAiEconomy / Support | Adaptive production, construction/resume, crews, ammunition and finite-stock cargo trips |
| PlayerAiState / AiPlannerState | Versioned planner checkpoint capture, validation and restore |

These layers run real decision code. They are a foundation for further tactics and tuning; combat quality, transport reliability and checkpoint equivalence still need behavioral validation.

## Information and decision rules

Enemy observations enter only through current visibility. Contact records freeze ID, owner, archetype, cell, roles, strength, value and observation tick. Hidden mobile contacts expire and their estimated strength decays; structures remain until their old cell is seen empty. The planner never resolves a remembered enemy through `World.Unit`. Hidden health, movement, orders and destruction do not update intelligence.

Exploration uses the player's fog history, or friendly viewing ranges without fog. Probes are reserved and retried after failure. Reachability and placement use existing physical engine services; they are not a separate fog-filtered navigation map. Dynamic physical obstacles therefore still influence these queries.

Capabilities derive from compiled weapons, movement, flight, cargo, repairs, builders and passenger facilities; policy tag overrides can supplement roles. Roles may overlap. Strength is an integer heuristic from damage, salvo, reload, current ammunition and HP, rather than a battle simulation. Objective utility combines observed value, structure/defense bonuses, distance, local danger, assignment load and expiring failure penalties. Stable utility ties use subject ID and cell order.

Posture reports Rebuild, Defend, Recon, Expand or Attack. Optional posture profiles now control defense reserves, assault commitments, scouts, specialist permissions and production/construction priorities. Production also uses observed threat counts. Optional seeded battle variety changes preferences without drawing from World RNG. No elapsed-time budgets or enemy-global strategic queries are used.

## Faction identity

Optional `factions[].doctrine` overrides global force sizing, readiness, timing, composition, scouting and
operation spacing. Ordered production/recruitment and observed-target preferences come from roles and
tags. Faction-specific weighted style lists retain battle variation within each identity. Null doctrine
retains global settings; null foundation ignores doctrines. Rules are copied and digested on construction.
See [faction doctrines](ai-faction-doctrines.md) for exact policy values, tunnel boarding/transit/evacuation,
air readiness/cover/strike/service behavior, dependencies and deferred acceptance scenarios.

## Battle variety

Optional `foundation.variety` adds small battle-to-battle differences. The shipped policy chooses a
weighted balanced, pressure, methodical or watchful style per player. Style biases production categories,
attack strength threshold, defense share and staging tempo by modest percentages. Stable seeded offsets
also vary combat goal counts, assembly/gather timing, strategic objective preference, probe travel scoring
and flank side/offset. Choices are stateless integer functions of battle seed, player, policy salt and
separate decision contexts; diagnostics cannot consume a draw or shift later choices.

This is varied behavior with reproducible inputs. A fresh host seed generally yields a different battle;
the same setup/match seed, AI seed, policy and inputs reproduce the same decisions. Similar choices can
still occur across seeds. There is no promise every trivial battle is unique or that any style is stronger.

The existing `new PlayerAi(playerId, policy)` uses the match seed. Hosts wanting a fresh personality on
the same map seed should choose a seed outside the simulation and pass
`new PlayerAi(playerId, policy, varietySeed: battleSeed)`. The client integration remains the host's job.
The SimRun AI runner chooses a fresh seed automatically for a new varied battle and prints BattleProfile
(seed and style) in its summary. Supply a fixed seed for reproducible generation:

```sh
dotnet run --project src/SimRun -- -player-ai content/ai/player.json -setup content/setups/player_ai.json -seed 42 -ai-seed 7 -fog true -ticks 2400
```

Normal command playback runs no AI and requires no fresh seed. Resume restores the saved battle seed
and never rerolls it; an explicitly supplied conflicting `-ai-seed` is rejected. Format 7 stores that seed.

Defaults: assembly ±60 ticks, staging ±40 ticks plus style bias, production priority ±10%, optional combat
goal counts ±1, objective utility ±75 on 1200-tick preference buckets, scout travel penalty 0–6 cells,
and lateral approach offset ±2 cells. Styles use weights 4/2/2/2 and scales within 90–115% in the shipped
policy. Preferences remain bounded by legality, queue/population caps, explicit goal maxima, essential
composition and job deadlines. Mandatory construction/builder/supply counts and explicit threat-counter
counts are not randomized. Emergency defense uses the normal decision schedule; HP, ammunition, damage,
stock, prices, movement and fog are unchanged. Null variety preserves the previous fixed preferences.

The current increment is compile-only; seed diversity, fixed-seed equality, checkpoint continuation and
behavior quality still require validation. The source implementation changes no World RNG state.

## Posture controls commands

`foundation.postures` is either empty (the original foundation behavior) or contains exactly one profile
for each posture. The shipped policy enables all five. `postureHoldTicks` prevents ordinary posture
changes for 200 ticks after a transition. A currently visible ground intruder near any friendly structure
can interrupt the hold and assessment interval immediately on the next decision; the existing strength
threshold can also select defense. The hold is a minimum dwell time, not a delayed response to attacks.

| Shipped posture | Defenders | Assault forces | Ground scouts | Forward strikes / insertions / construction |
| --- | --- | --- | --- | --- |
| Rebuild | 60% | 0 | 0 | disabled |
| Defend | 80% | 0 | 0 | disabled |
| Recon | 25% | 0 | 2, capped by outer scoutMissions | disabled |
| Expand | 30% | 1 | 1 | enabled |
| Attack | 15% | 4 | 1 | enabled |

The guard target rounds up the percentage of current mobile ground fighters, with `homeGuards` as a
minimum and the live fighter count as its ceiling. Defense allocation runs before specialist assignment
and ordinary mission updates. Excess assaults retain active forces before staged/assembling forces,
then lower IDs; recalled forces become defenders if needed and reachable, otherwise recover. A defense
gap can also recall a permitted assault. Whole missions/squads move between roles rather than splitting
squads to hit percentages. This can over-reserve a force, and unreachable or unready units may prevent
meeting the target. Surplus whole guard missions may assemble offensively when permission returns.
New scouting and assault missions obey profile caps; returning missions still occupy normal mission slots.
Strategic recalls do not add failed-objective penalties.

A visible home emergency receives defense commands before the rotating mission budget. Rebuild,
Defend and Recon prohibit distant air/artillery strikes and new forward passenger insertions. Visible
targets near friendly structures remain eligible for defensive strikes. Existing offensive specialist jobs
cancel through normal Stop/ReturnToBase commands; radar and air cover move their post home. A transport
cancels unboarded orders and unloads boarded passengers before releasing ownership. This cancellation
requests unloading at its current location, so terrain/landing legality can delay release; safe landing-zone
selection remains future work. Aircraft reconnaissance and service remain available during defensive postures.

`combatPriority`, `counterPriority`, `infrastructurePriority`, `logisticsPriority`, `reconPriority` and
`defensePriority` weight desired-count deficits. Construction now uses those weighted deficits too;
ties retain policy order. Builders/producers count as infrastructure, armed structures as defense,
supply/cargo/service/repair as logistics, radar/unarmed scouts as recon and other units as combat.
Armed transports use the larger combat/logistics weight. A goal countering a known threat adds the
counter bonus; zero total weight skips the goal. These priorities do not invent currency, cancel engine
production queues or abort an unfinished site. Construction still resumes its current site first.
Forward goals use home placement while forward permission is disabled.

## Forces and coordinated operations

Mission membership is exclusive. Existing squads are recruited whole when they fit; roles recruit before general filling. Full composition and minimum force size permit an early launch. At the assembly deadline, essential composition still applies; unavailable essentials cancel the assembly rather than waiting forever. Optional quotas may be relaxed. A member with multiple roles may satisfy multiple quotas.

Mobile assault membership excludes builders, cargo, deployed weapons, aircraft, strike bodies and units unavailable through boarding/service/production workflows. Units must be free, healthy, armed and past their rest period. A limited home guard and independent scout missions remain. Compatible infantry can form squads and switch March/Assault with distance hysteresis.

An assault retains a reachable objective. Known structures and legal visible ground targets precede exploration. A force can join a bounded operation: forces stage on alternating approaches, chosen using reachability and danger. The operation releases when its desired forces arrive or the staging deadline expires. Its lifetime or lost intelligence triggers recovery. This coordinates orders; it does not guarantee simultaneous arrival through actual terrain or perform route-wide danger optimization.

HP loss, member loss, empty ground weapons, expiration and stalled progress trigger recovery toward a reachable stocked supplier or home. Release stops lingering orders and applies rest. Bounded outcome history penalizes recently failed objectives; this is deterministic short-term feedback, not learned policy weights.

## Sustainable force loop

Optional `foundation.forceManagement` enables the M1 force-loop changes; null preserves the previous
force-assignment path. In safe Attack posture with a remembered structure objective, `preserveAttackForce`
can reduce the home target enough to leave the larger of `forceMinimum` and the largest complete fighter
squad for an offensive. Existing threat/posture rules remove that exception during an emergency. This
prevents the last whole squad being kept home merely to satisfy a small numeric reserve. It is a policy
choice to take offensive risk, not a guarantee that home is safe.

Spare defense groups can move into assembling assaults. Loose units move individually; complete squads
move together, and an incomplete/external squad is left alone. Remaining defenders must still meet the
current target. Health, ammunition, current local threats, pending commands, command ownership and home
reachability gate transfers. Source/recipient order caches are invalidated; an emptied source mission
finishes without resting transferred units or recording a spurious defeat.

A full-sized force may relax optional composition preferences before the assembly deadline; essential roles still apply.

Local reinforcement runs every 100 ticks, or earlier when the ready-unit count grows, before creating new missions. Safe active/staging assaults below
force size receive nearby ready groups within 20 cells, subject to common objective reachability. Missing
composition is prioritized, then smaller forces and mission ID. Each recipient examines at most 32 nearby
groups and the whole decision transfers at most 8 groups, including earlier defense rebalancing. No new
reinforcement march across the map is invented. The original progress timer remains intact and launch
strength uses the larger historical/current member count, so replacements do not erase a stall or fake losses.

`unarmedScouts` permits free healthy ground observers, excluding builders, cargo, aircraft, deployed weapons
and dedicated non-ground weapon platforms. Unarmed scouts use Move for probes and do not immediately
recover for having no ammunition. Other depletion, observation and mission deadlines still apply.

Vehicle crewing now has up to 16 persistent driver jobs lasting at most 600 ticks. Jobs reserve the driver,
retry rejected idle assignments and cancel with Stop if the target disappears, gets another driver or the
deadline expires. Success releases planner ownership after normal engine boarding. A cancelled driver rests
for at least the input delay before reassignment; its queued Enter cannot silently become a later assignment.
No boarding or driver state is changed directly.

`Capabilities` reports the last-observed friendly prerequisites separately from configured policy:
construction, production, builder/infantry replacement, resupply, aircraft, local reinforcement, reconnaissance
and checkpoints. Missing training chains are explicit content dependencies. This read-only diagnostic does
not influence commands, prove route/queue availability or claim milestone completion. SimRun includes it and
CrewJobs in its trace. M1 acceptance and pending evidence are tracked separately from code presence.

## Force tactics (stacked follow-up)

The optional `foundation.tactics` policy enables a mobile ground-force executor. Null preserves the
audited mission executor. The shipped profile checks at most 12 nearby candidates per force, selected
by distance from its lead and then ID. Candidate observation scans still scale with visible units.
Only currently visible ground enemies near a member are eligible. A focus order requires legal,
in-range weapons on at least 50% of the force; the engine's weapon selection checks ammunition,
armor compatibility, minimum/maximum range and line of fire.

Target score combines summed armor-adjusted weapon effectiveness, a return-fire bonus when the
visible enemy targets a member, the persistent structure objective, a current-focus bonus, missing-HP
percentage and travel distance. Equal scores prefer lower IDs. Attack applies to the whole force,
preserving squads; a quorum does not imply every member can shoot. Defenders restrict candidate
targets to threats near a friendly structure. Enemy state is read only from frozen/current visible
observation lists, and losing visibility prevents new target selection. Ordinary engine orders already
issued still obey engine combat rules. Focus fire neither simulates projectile arrival nor allocates
shots to prevent overkill, and is not cover-aware micro.

Out of local contact, an active assault spread more than 12 cells from its center can regroup. It tries
up to 8 actual member positions near that center and checks every member's movement class for
reachability. The persistent mission/operation objective remains unchanged. The rally uses AttackMove,
ends on arrival or after 160 ticks, and waits 400 ticks before another attempt. Nearby visible contact
or a legal focus target interrupts regrouping immediately; existing mission expiration, depletion and
progress deadlines continue to apply. A blocked rally can therefore expire rather than holding the
army forever. The candidate query uses normal physical navigation services, not hidden enemy state.

Tactical orders use the existing command cap and a 20-tick retry interval, never shorter than input
delay. Mission views expose the rally point/deadline; checkpoints retain them and the retry
clock. This follow-up received compilation only, per the original request; Astra's earlier fixture/CI
results apply to the parent revisions, not to the new tactical behavior.

## Specialist rules

Reservations prevent force recruitment from competing with specialist actors and passengers. Every job has a deadline and command retry schedule.

- Aircraft receive search areas, air cover or visible weapon-compatible strike targets. Low fuel/ammunition or poor health requests normal Rearm service, including upgrading an automatic return to full service. Existing flight/airfield systems execute takeoff, landing and service.
- Artillery approaches a remembered ground objective at standoff, respecting the chosen weapon's minimum/maximum range, and fires only at a currently visible legal target. Deployable units stow before moving. Radar and dedicated air-defense units take forward posts; depleted weapons release for ordinary resupply.
- Damaged vehicles seek compatible friendly garages. Repair units follow compatible damaged allies and rely on the engine's repair effect. Available combatants can escort active cargo trucks with Guard.
- Ground carriers and rotary transports reserve loose, legal passengers. Jobs progress through pickup/boarding, travel/insertion and unload. Boarding has a timeout; unboarded passengers receive cancellation orders. Expired occupied carriers keep their reservation while unloading. Aircraft/ground landing legality and passenger egress remain engine decisions. Fixed-wing airlift vehicles are not handled by this executor.
- Charged launch abilities can create observation aircraft or launch strike bodies against visible targets. The bot uses UseAbility and normal cooldowns/stocks. Mine placement, garrison/tunnel plans, ambush design and aircraft loadout switching remain follow-ups.

## Production, construction and logistics

Faction goals identify lobby presets or slotless archetypes. Unavailable definitions are skipped. `afterTick` delays goals. `against`, `extraPerThreat` and `maximum` increase desired counts from known Ground/Air/Armor/Structures threats, with a ceiling. Production prioritizes normalized deficits, ties in policy order, issues at most one order per decision and observes producer tags, queues, exclusivity, aircraft station availability and the army cap including drivers. Aircraft automation requires `foundation.aircraft`; the producer and normal engine still decide legality.

With `foundation.buildPlanning: null`, construction retains the audited single-project/global-cursor executor. The shipped policy enables independent construction recovery instead:

- Placement starts near the home/forward anchor and expands through deterministic square rings, then other facings. Each policy goal owns a cursor and retry clock. At most 4 goals receive 4 candidate checks each, sharing the outer `buildChecksPerDecision` limit. A goal yields for 40 ticks after a slice, and for 600 ticks after a full failed sweep. Lower-ranked goals can progress while higher-ranked ones wait. Anchor changes restart geometry without clearing a cooldown.
- Site records track actual `Unit.Work`, not elapsed assignment time. No work for 600 ticks permits a resume retry, preferring a free replacement worker before retrying an assigned one. Access is checked near the footprint edge rather than the blocked center. Retry delay is 200 ticks. Existing progress resets the stall clock; a failed resume does not.
- At most 2 unfinished projects are allowed, counting other sites too. A progressing configured project prevents opening another; a stalled project can yield to another goal. Passive runway segments are excluded from independent project handling while their lead row builds them.
- `cancelAbandoned` permits normal CancelBuild after 6000 ticks without observed work. Only unfinished sites matching configured construction goals are eligible. Cancellation keeps an input reservation and delays the same definition before rebuilding. It can still run when all workers are lost. The host AI owns the player: hybrid human/AI ownership of the same construction goals is not supported.
- The lowest-ID available builder is preserved from unrelated specialist work while construction is needed. A repair job can yield through normal cancellation; ordinary post-job rest still applies. This protects a builder rather than manufacturing one. Missing construction capabilities still require actual producers/content.
- A configured facility that could satisfy an unmet production goal receives a 500-point construction priority bonus if no current compatible operational producer exists. Queue backlog alone does not request another facility. Existing goal counts, unlock delays, visibility, enemy-distance checks and engine CanPlace remain authoritative.

During Rebuild or a stalled project, construction recovery receives the economy command slot before ordinary production.

`BuildPlans` and `ConstructionSites` expose detached diagnostics; SimRun includes them in its trace. Failed sweeps,
site retries, abandonment and builder yields have trace reasons. This code has compilation-only local validation;
blocked maps, cancellation, construction fairness and checkpoint continuation still need behavioral checks.

Crew jobs board legal soldiers into empty friendly vehicles near home. Cargo reserves sources/destinations, uses LoadCargo/Unload, respects finite stock and cancels expired trips. Dry weapons seek stocked friendly suppliers. No direct spawning, free refills or invented credits are used. Engine Charge/Refund currently do not spend credits, and infantry replenishment requires a real producer in the supplied content.

## Policy contract

Policy version remains 1. `foundation: null` preserves the original ground policy path; the shipped policy enables the new layer. Strict camelCase JSON rejects unknown fields and invalid limits. Durations are ticks (20/second), distances whole cells, percentages 0–100. This differs from simulation content's tenths of cells.

| Policy group | Main controls |
| --- | --- |
| Original decision/mission fields | decisionTicks, commandsPerDecision, maxMissions, missionsPerDecision, forceSize/Minimum, assembly/mission/stall/recovery/restTicks |
| Original combat/exploration | homeGuards, scoutMissions, defend/arrival/progressCells, readiness/retreat percentages, region/rescout/retry/reach limits, squad hysteresis |
| Original economy/logistics | factoryQueue, armyCap, build retry/radius/spacing/check budget, supply threshold/trip radius |
| foundation strategy | strategyTicks, contactTicks, maxContacts/Objectives, threatCells, strength percentages, utility weights/bonuses, assignmentPenalty |
| foundation posture control | postureHoldTicks; one postures profile per posture with defenderPercent, assaultForces, scouts, spending priorities and distantStrikes/insertions/forwardBuild permissions |
| foundation construction | buildPlanning goalsPerDecision/candidatesPerGoal, sliceRetryTicks/failedSweepTicks, siteStallTicks/siteRetryTicks/abandonTicks, maxSites/builderReserve, missingProducerBonus, cancelAbandoned |
| faction doctrine | force/composition/readiness/timing/operation overrides, roles/target roles/tags, styles, tunnel search/lifecycle bounds, sortie/cover/recon/infrastructure controls |
| foundation variety | variety seedSalt/styles, assembly/gather jitter, production priority/combat count offsets, objective/preference interval, scout/approach offsets |
| foundation force loop | forceManagement reinforcementTicks/reinforceCells/groupTransfers/candidateGroups, preserveAttackForce, unarmedScouts, crewTicks/crewAssignments |
| foundation tactics | targetCandidates, engageCells/Percent, orderTicks, weapon/finish/distance weights, returnFire/objective/focus bonuses, regroupCells/Ticks/RetryTicks, rallyCandidates |
| foundation composition | composition role/minimum/essential; roles role/anyTags overrides |
| foundation operations | maxOperations, operationForces/GatherTicks/Ticks, stageCells, flankCells, approachChecks |
| foundation specialists | specialistJobs/Ticks/OrderTicks, airFuel/AmmoPercent, airAreaCells, artilleryCells, serviceHpPercent, boardingTicks, transportPassengers |
| foundation feedback/expansion | expansionCells, outcomeCapacity/Ticks, failurePenalty; aircraft/transports/expansion feature gates |
| factions production/construction | unit/count plus against, extraPerThreat, maximum, afterTick, forward |
| traceCapacity | Bounded recent diagnostic events; zero disables them without changing decisions |

Policy lives outside hashed `content/mvp`. It changes generated commands, not historical command playback or the simulation content hash. Count and candidate limits bound retained state and explicit searches; friendly-unit scans and existing reachability queries still scale with world size. No think-time performance guarantee has been measured.

## Checkpoint contract

`CaptureState` format 7 includes tunnel trip stages/retry clocks and specialist operation associations, battle seed identity, reinforcement/construction/posture/regroup clocks, crew reservations, serials, probes, intelligence, strategy/objectives/outcomes, cooldowns, pending reservations, missions, cargo, operations and specialists. `RestoreState(world, state)` requires a fresh bot and rejects mismatched player, world tick/hash, match digest, content hash, policy digest or format before mutating planner fields. It checks list limits, IDs, enum ranges and exclusive mission/specialist ownership. Diagnostics and scratch observations are excluded; the next decision rebuilds observations.

`PlayerAiStateLoader` serializes single or all-player checkpoints. A planner checkpoint is **not a world save**: the host must separately restore the exact world and pending command queue. World.Hash does not replace checking that queue. SimRun pairs all-player state with a replay ending at the checkpoint tick, retaining commands still in flight; resume replays that world, validates each planner and continues. Formats 1–6 are rejected rather than silently losing battle variation, reinforcement, crew, construction, posture or regroup state. Formats and the manually maintained policy digest must be updated when decision state changes. The audit adds JSON round-trip and exact command/world-hash continuation checks, including commands pending at the checkpoint.

## Validation and remaining work

The original foundation expansion was compile-only. The 2026-10-30 audit adds behavioral regressions for transport cancellation, specialist cleanup, reinforcement orders, cargo ownership, garage access, foundation fog isolation and exact serialized checkpoint continuation. Strategic-control regressions additionally check emergency recalls, defensive reinforcements, strike permissions, insertion cancellation and production priorities. The full solution test suite is run for this audit. Representative-match tuning, large-map profiling and platform CI remain separate validation.

Next work includes behavioral fixtures, match tuning, large-map profiling, infantry production when supported, richer tactical terrain/ambush/garrison plans, resource budgeting when the engine has an economy, and allied coordination. This is broad implemented planner groundwork, not a claim of a complete competitive opponent.

## PR 19 audit corrections (2026-10-30)

Transport cancellation stops the carrier and all waiting passengers, including boarding inputs still in flight, in one command-budget slot. The planner retains their reservations until cancellation executes and boarded passengers disembark. An incomplete boarding group now aborts at the boarding deadline instead of releasing passengers while their Enter orders can still execute.

Ending a specialist task cancels its persistent movement, guard, fire or air task before release. Combat forces invalidate the last issued order when membership grows, so idle or already-guarding reinforcements receive the force objective. Cargo trips reserve their truck against specialist assignment, and checkpoint validation includes cargo in exclusive ownership checks. Garage service tests reachability at production door exits rather than the blocked building centre.

`PlayerAiFoundationTests` covers delayed/partial transport cancellation, specialist cleanup, reinforcement orders, cargo ownership, garage access and serialized checkpoint continuation with pending replay inputs. These are regression checks, not a balance or win-rate evaluation.

## PR 20 audit corrections (2026-10-30)

Distant-strike permission also gates fallback support fire. An insertion revoked by posture changes enters cancellation before the specialist retry clock, stopping the carrier as well as waiting passengers. Growing defensive forces invalidate their last order so new members guarding elsewhere receive the home objective.

`PlayerAiStrategyTests` covers permitted/prohibited distant fire, cancellation with a future retry clock, whole-squad emergency recall during posture hold, defensive reinforcements and weighted production choice. The foundation checkpoint continuation fixture also runs with the shipped posture profiles.
