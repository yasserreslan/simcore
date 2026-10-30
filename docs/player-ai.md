# Player AI foundation

`Bots/PlayerAi` is an opt-in, host-owned opponent planner. It emits ordinary delayed commands; the engine remains responsible for movement, combat, visibility, production, boarding and servicing. Existing `Bot` and `SkirmishAi` behavior stays separate. The reference study is in [rts-ai-research.md](rts-ai-research.md); this implementation does not import reference-game code.

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
| PlayerAiOperations | Multi-force staging, alternative approaches, synchronized release and withdrawal |
| PlayerAiSpecialists | Aircraft, repair/service, artillery, radar/air defense and cargo escorts |
| PlayerAiTransport | Passenger reservations, pickup, boarding, insertion, unload and cancellation |
| PlayerAiAbilities | Charged launch abilities for observation aircraft or visible-target strike bodies |
| PlayerAiEconomy / Support | Adaptive production, construction/resume, crews, ammunition and finite-stock cargo trips |
| PlayerAiState / AiPlannerState | Versioned planner checkpoint capture, validation and restore |

These layers run real decision code. They are a foundation for further tactics and tuning; combat quality, transport reliability and checkpoint equivalence still need behavioral validation.

## Information and decision rules

Enemy observations enter only through current visibility. Contact records freeze ID, owner, archetype, cell, roles, strength, value and observation tick. Hidden mobile contacts expire and their estimated strength decays; structures remain until their old cell is seen empty. The planner never resolves a remembered enemy through `World.Unit`. Hidden health, movement, orders and destruction do not update intelligence.

Exploration uses the player's fog history, or friendly viewing ranges without fog. Probes are reserved and retried after failure. Reachability and placement use existing physical engine services; they are not a separate fog-filtered navigation map. Dynamic physical obstacles therefore still influence these queries.

Capabilities derive from compiled weapons, movement, flight, cargo, repairs, builders and passenger facilities; policy tag overrides can supplement roles. Roles may overlap. Strength is an integer heuristic from damage, salvo, reload, current ammunition and HP, rather than a battle simulation. Objective utility combines observed value, structure/defense bonuses, distance, local danger, assignment load and expiring failure penalties. Stable utility ties use subject ID and cell order.

Posture reports Rebuild, Defend, Recon, Expand or Attack. Optional posture profiles now control defense reserves, assault commitments, scouts, specialist permissions and production/construction priorities. Production also uses observed threat counts. No random draws, elapsed-time budgets or enemy-global strategic queries are used.

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

## Specialist rules

Reservations prevent force recruitment from competing with specialist actors and passengers. Every job has a deadline and command retry schedule.

- Aircraft receive search areas, air cover or visible weapon-compatible strike targets. Low fuel/ammunition or poor health requests normal Rearm service, including upgrading an automatic return to full service. Existing flight/airfield systems execute takeoff, landing and service.
- Artillery approaches a remembered ground objective at standoff, respecting the chosen weapon's minimum/maximum range, and fires only at a currently visible legal target. Deployable units stow before moving. Radar and dedicated air-defense units take forward posts; depleted weapons release for ordinary resupply.
- Damaged vehicles seek compatible friendly garages. Repair units follow compatible damaged allies and rely on the engine's repair effect. Available combatants can escort active cargo trucks with Guard.
- Ground carriers and rotary transports reserve loose, legal passengers. Jobs progress through pickup/boarding, travel/insertion and unload. Boarding has a timeout; unboarded passengers receive cancellation orders. Expired occupied carriers keep their reservation while unloading. Aircraft/ground landing legality and passenger egress remain engine decisions. Fixed-wing airlift vehicles are not handled by this executor.
- Charged launch abilities can create observation aircraft or launch strike bodies against visible targets. The bot uses UseAbility and normal cooldowns/stocks. Mine placement, garrison/tunnel plans, ambush design and aircraft loadout switching remain follow-ups.

## Production, construction and logistics

Faction goals identify lobby presets or slotless archetypes. Unavailable definitions are skipped. `afterTick` delays goals. `against`, `extraPerThreat` and `maximum` increase desired counts from known Ground/Air/Armor/Structures threats, with a ceiling. Production prioritizes normalized deficits, ties in policy order, issues at most one order per decision and observes producer tags, queues, exclusivity, aircraft station availability and the army cap including drivers. Aircraft automation requires `foundation.aircraft`; the producer and normal engine still decide legality.

Construction resumes a useful unfinished site before creating another project. Bounded placement searches check reachability, visible centers, visible enemy distance and `World.CanPlace`. The foundation cycles four facings and can anchor `forward` goals toward the known front during Expand/Attack. One unfinished project is allowed at a time. The first unmet placeable-definition goal retains the search cursor, so a goal with no legal site can delay later goals; adaptive base-layout planning is not implemented.

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
| foundation composition | composition role/minimum/essential; roles role/anyTags overrides |
| foundation operations | maxOperations, operationForces/GatherTicks/Ticks, stageCells, flankCells, approachChecks |
| foundation specialists | specialistJobs/Ticks/OrderTicks, airFuel/AmmoPercent, airAreaCells, artilleryCells, serviceHpPercent, boardingTicks, transportPassengers |
| foundation feedback/expansion | expansionCells, outcomeCapacity/Ticks, failurePenalty; aircraft/transports/expansion feature gates |
| factions production/construction | unit/count plus against, extraPerThreat, maximum, afterTick, forward |
| traceCapacity | Bounded recent diagnostic events; zero disables them without changing decisions |

Policy lives outside hashed `content/mvp`. It changes generated commands, not historical command playback or the simulation content hash. Count and candidate limits bound retained state and explicit searches; friendly-unit scans and existing reachability queries still scale with world size. No think-time performance guarantee has been measured.

## Checkpoint contract

`CaptureState` format 2 includes the posture hold clock and copies other clocks, serials, probes, intelligence, strategy/objectives/outcomes, cooldowns, pending reservations, missions, cargo, operations and specialists. `RestoreState(world, state)` requires a fresh bot and rejects mismatched player, world tick/hash, match digest, content hash, policy digest or format before mutating planner fields. It checks list limits, IDs, enum ranges and exclusive mission/specialist ownership. Diagnostics and scratch observations are excluded; the next decision rebuilds observations.

`PlayerAiStateLoader` serializes single or all-player checkpoints. A planner checkpoint is **not a world save**: the host must separately restore the exact world and pending command queue. World.Hash does not replace checking that queue. SimRun pairs all-player state with a replay ending at the checkpoint tick, retaining commands still in flight; resume replays that world, validates each planner and continues. Format 1 checkpoints are rejected rather than silently losing the posture hold clock. Formats and the manually maintained policy digest must be updated when decision state changes. The audit adds JSON round-trip and exact command/world-hash continuation checks, including commands pending at the checkpoint.

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
