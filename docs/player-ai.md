# PlayerAi v1: ground opponent policy and host integration

PlayerAi is the first persistent player-level planner. It is opt-in; SkirmishAi and the random replay Bot keep their existing behavior. See [the source research](rts-ai-research.md) for OpenRA, Petra, NullBot, and Generals ideas that informed it.

## Run and integrate

From the repository root:

```sh
dotnet run --project src/SimRun -- -player-ai content/ai/player.json -setup content/setups/player_ai.json -fog true -ticks 2400 -save /tmp/player-ai.json -ai-trace /tmp/player-ai-trace.json
dotnet run --project src/SimRun -- -replay /tmp/player-ai.json -every 0
```

The demonstration lobby adds workers to the battle roster so the bot can construct a garage and then produce units. The headless runner currently accepts flat lobby setups and controls every player with the same policy. A client host may give PlayerAi a World with terrain directly. Prepare routing caches during loading if the map needs it; the bot uses the existing class-specific reachability service.

```csharp
var policy = PlayerAiLoader.Load("content/ai/player.json");
var bot = new PlayerAi(playerId, policy);
// Each host tick, before stepping the world:
foreach (var command in bot.Think(world))
    RecordAndBroadcast(world.Issue(command));
world.Step();
```

Think returns normal commands at World.InputDelay; it does not issue them or mutate units. The host owns one instance per AI-controlled player per World. It must call it only on the authoritative command-generating host. Repeated calls on one tick return no extra commands. During playback, use the recorded commands and do not run the AI.

Private bot memory is outside World.Hash, just as the existing bots' memory is. Live save/reconnect of bot memory is not implemented: a new instance is not an exact continuation. Normal command replay is supported. Rules are copied on construction; mission snapshots are detached copies. Diagnostics do not affect command choices.

## Information rules

The observation boundary reads friendly units and currently visible enemies. Structure memory retains only IDs and last observed cells. It never resolves a remembered enemy through World.Unit. It forgets a structure only when the old cell is currently visible and the structure is absent. Hidden movement, health changes, orders, and destruction therefore do not update intelligence directly.

Exploration uses the side's own fog history. Without a fog grid it timestamps probes in friendly viewers' ranges. Each scout or assault search reserves a probe; unseen probes precede older seen probes, then distance, then row index. Connectivity tests use existing routing data and the acting unit's movement class. They are terrain/physical reachability tests, not enemy reconnaissance. Dynamic physical obstacles still affect the normal engine routing and placement queries; this is not a separate fog-filtered copy of the navigation map.

All choices visit units in ID order and cells/probes in fixed index order. Distance ties retain the lower ID or index. The planner draws no randomness and changes no engine RNG state.

## Combat missions

Mission membership is exclusive. Existing squads are recruited as a whole when they fit; no mission steals one member from another squad. One small home guard is retained when other combatants exist. When only its defenders remain, it becomes an assault force rather than waiting forever at home.

Independent scouts reserve reachable probes. Other available mobile ground fighters assemble in forces. Builders, cargo trucks, deployed weapons, aircraft, one-way strike units, units leaving production doors, and units in garage service are excluded from mobile assault membership. New members must be idle/guarding, adequately healthy, armed, and out of their post-mission rest period.

An assault launches at the minimum force size or its assembly deadline; the deadline permits a smaller nonempty force. It prefers the nearest reachable remembered structure, then a reachable visible ground enemy, then an exploration probe. The objective persists. Passive structures receive explicit Attack commands only while visible and compatible with at least one member's weapon. Compatible infantry forces may FormSquad; existing squad systems handle their slots and combat. Their mode switches to Assault near a visible enemy and back to March beyond the larger hysteresis radius.

A depleted force returns to the nearest reachable stocked supplier, or home. Low aggregate HP, sufficient member losses, all ground weapons empty, mission expiration, or a progress timeout trigger recovery. A visible fight refreshes the progress timer. A failed probe is deferred for a retry period. Recovery ends at arrival or its own deadline; release stops lingering orders and applies the rest period. Expired cargo jobs likewise cancel their order before releasing a reservation.

Support weapons fire only at currently visible ground targets accepted by existing weapon checks. Dry units seek a nearby friendly supplier; deployable weapons stow first. Air-defense specialists retain the normal engine's target acquisition. Full aircraft strategy is outside v1.

## Production, construction, and supply

Faction plans contain ordered desired counts. Names are lobby preset names or slotless archetype IDs; unavailable entries are skipped, so a policy can be used with another lobby without inventing illegal definitions.

Production counts alive units, waiting orders, occupied bays/doors, and commands still in flight. It issues at most one production order per decision, observes the factory's tag restrictions and queue limit, and counts produced drivers against the army cap. Only ground production without into-station aircraft handling is automated in v1. No SandboxSpawn or direct World.Spawn is used.

Construction resumes a planned unfinished site with an available reachable worker before creating another site. A new site must satisfy World.CanPlace. Its centre must be visible and outside the configured radius of visible enemies. A fixed placement cursor searches a bounded number of candidates per decision; there is at most one unfinished construction project before starting another. Retry delays prevent repeated failed commands. Locations currently use facing zero; rotation-aware strategic base layouts are follow-up work.

Cargo trucks reserve low-stock friendly destinations, or load from a reachable friendly cargo source when empty. One cargo job of each kind owns a store at a time. Trip distance and duration are bounded. A lost/captured/finished target releases the job; expiration stops it and rests the truck. Trucks retain any stock the engine cannot unload, and sources use the normal finite-stock rules.

The engine's Charge/Refund hooks currently do not spend credits. V1 deliberately uses desired counts and a total population cap; it does not claim to manage a working monetary economy. It also cannot replenish infantry until the game provides an appropriate producer.

## Policy reference

All distances in this file are whole cells; durations are simulation ticks (20 per second). This differs from the tenths-of-a-cell units of the main content schema. Unknown policy fields and invalid limits are errors. Version is currently 1.

| Fields | Rule |
| --- | --- |
| decisionTicks, commandsPerDecision | Decision interval and hard command-record cap. Combat missions rotate; exhausted command capacity defers work. |
| maxMissions, missionsPerDecision | Simultaneous combat missions and update count per decision. |
| forceSize, forceMinimum, assemblyTicks | Maximum force size, desired launch minimum, and maximum assembly wait. |
| missionTicks, stallTicks, recoveryTicks, restTicks | Mission lifetime, lack-of-progress timeout, return timeout, and reassignment delay. |
| homeGuards, scoutMissions | Reserved defense count and independent scout count (zero disables either). |
| defendCells, arrivalCells, progressCells | Defense range, arrival tolerance, and minimum distance improvement. |
| readyHpPercent, retreatHpPercent, retreatMembersPercent | Recruitment and retreat thresholds; retreat HP must be below readiness HP. |
| regionCells, rescoutTicks, probeRetryTicks, reachSearchCells | Exploration grid, revisit/failed-probe delays, and passable-endpoint search radius. |
| assaultCells, marchCells, formSquads | Formation hysteresis and compatible infantry squad creation. |
| factoryQueue, armyCap | Queue depth and alive/pending population cap for deliberate production. |
| buildRetryTicks, buildRadiusCells, buildSpacingCells, buildChecksPerDecision | Placement retries, search bounds, spacing, and work cap. |
| supplyLowPercent, supplyTripCells | Restock threshold and automatic trip distance limit. |
| traceCapacity | Bounded recent events; zero disables tracing without changing commands. |
| factions[].production / construction | Ordered unit/count goals; all entries must have positive bounded counts and unique names within their list. |

Policies live outside content/mvp. Changing them changes future AI commands, but not the simulation content hash or old replay playback. A replay stores the commands, not a promise to regenerate them with an edited policy.

## Review and follow-ups

PlayerAiTests covers command-only behavior, idempotent decisions, hidden-state independence, remembered hidden destruction, assembly deadlines, recovery, production/driver caps, pending commands, cargo reservations, builder replacement, whole-squad reservations, strict policy validation, diagnostic independence, and bot-free command replay. These are small fixtures; they establish contracts, not competitive strength.

Per the owner's request, local validation uses a build and focused checks only; the full existing replay and platform suite runs in CI. No new benchmark or large local match is required for this PR. Follow-ups should measure match quality and large-map think cost, add specialist reconnaissance/air missions, role-specific force requirements and counter-production, live bot state persistence, and more strategic base placement.
