# Faction AI doctrines

Status: implemented foundation; compilation reviewed, behavioral validation pending under the owner's
no-local-tests instruction. This is part of [M1](ai-milestone.md), not a competitive-strength claim.

The shared planner still owns visibility, construction, production, force ownership, missions,
operations, recovery and normal delayed commands. Optional `factions[].doctrine` supplies identity.
No faction name is hardcoded into the executors; only the policy selects the mechanics and preferences.

| Decision | Resistance: mobile-network-raiding | Sentinel: combined-arms-air-support |
| --- | --- | --- |
| Ground force | Nominal 6, launch minimum 3; shorter assembly and mission windows | Nominal 8, launch minimum 6; longer preparation and persistence |
| Reconnaissance | Up to 2 independent ground scouts; favor observers and radar | Up to 1 ground scout plus bounded low-cost air reconnaissance |
| Ground composition | Light frontline, optional antiarmor, rockets and artillery | Larger frontline, optional antiarmor, tanks and radar support |
| Objectives | Extra value for observed logistics, radar and artillery | Extra value for observed air defense, radar and artillery |
| Operations | One force, wider lateral approaches | Up to two coordinated forces, narrower approaches |
| Signature tool | Linked tunnel construction and mission transport | Helipads/airfield priority, readiness, cover and repeated sorties |
| Battle styles | Fast raids, recon network, rocket pressure | Armored advance, air support, secure buildup |

These are nominal settings before the existing seed-based variation and posture constraints. A complete
existing squad may exceed the nominal force size up to the engine's 64-member bound; it is not split.
Emergency defense still takes precedence. Null doctrine uses global settings; null foundation disables
doctrines. Null variety keeps fixed doctrine preferences, while enabled variety selects within that
faction's style list. The policy is copied on construction and covered by the compatibility digest.

## Resistance tunnels

Construction first requests a large home entrance, then forward medium and infantry entrances once a
front is known and forward building is permitted. Placement must link an existing standing compatible
node; this is incremental network extension, not a search for an optimal map-wide tunnel layout.

Assault, staging and recovery movement can choose a tunnel route. The planner checks owned standing
entry/exit nodes, unit size, door access, onward ground connectivity, network capacity, other mission
reservations and known exit danger. Estimated travel includes approach, underground links, throughput,
gates and travel from the exit. A reachable surface route must lose at least 80 estimated ticks to justify
tunneling; a disconnected surface destination may use a valid tunnel route directly. Estimates use straight
surface distance, not an exact timed path. Vehicles can traverse medium junctions where the engine permits
it, but entry/exit still requires their actual size.

A mission retains its members through boarding, transit and evacuation. It retries ordinary Enter and
TunnelGo commands, checks deadlines, destroyed entrances and changed exit danger, and cancels pending
boarding before selecting an emergency exit. Evacuation prefers known lower danger, then proximity to home.
If physically trapped, units remain reserved and the planner retries when the friendly network changes.
Defense allocation, reinforcement and completion cannot take members out of a live trip.

The shipped search examines at most 32 owned nodes, 4 candidates at each end and 12 endpoint pairs per
attempt, with a 100-tick retry interval. Boarding is bounded by 600 ticks and a trip by 2400 ticks.
The engine remains authoritative for capacity, travel, collapse and command legality.

## Sentinel aircraft

The policy requests helipads and attack helicopters earlier, then the existing airbase core and jets when
legal facilities fit. The core creates its own dependent pieces through the engine. It is too large for
some small lobbies; the AI does not bypass placement rules or fabricate stations. Ground armor production
also waits for compatible real garages. The Sentinel radar goal uses its actual `radar_truck` roster entry.

Sorties require at least 45% fuel, 60% weapon ammunition and 75% health. Ammunition readiness counts each
weapon stock pool once, excluding unrelated ability stores. Known air-defense danger is compared with
attacker strength. At most two attackers are assigned to one observed target; weapon compatibility and
posture permissions still apply. When enemy aircraft are known, nonfighter attackers require active
fighter cover. Cover is linked to a ground operation when available and follows its forces with movement
hysteresis. Operation withdrawal, changed strike danger or depleted readiness causes recall or servicing.

Up to two cover jobs and one reconnaissance job can run; aircraft over the configured scouting cost
ceiling wait for useful combat work rather than becoming disposable scouts. Aircraft held at a station
following recall can receive a new sortie once ready. Rearming, fuel, repair, launch slots and landing
remain ordinary engine mechanics. This is not an exact fuel-to-target model, coordinated strike package
scheduler, or omniscient air-defense avoidance system.

## Review and acceptance

`FactionDoctrine`, battle profiles, mission tunnel snapshots, specialist operation IDs, capabilities and
runner traces expose intent and blockers. Planner checkpoint format **8** retains local combat/recovery clocks as well as tunnel phases/retry
clocks and specialist operation associations; older formats are rejected. These are external host planner
records, not new simulation state or changes to World RNG.

Deferred scenarios include tunnel savings/capacity contention, heavy units through medium junctions,
collapse during pending boarding, changing exit danger, evacuation and checkpoint continuation in every
trip phase. Air scenarios include insufficient facilities, repeated launch/service cycles, changing AA,
loss of cover, withdrawal and ammunition pools. Compare several seeds within each faction and both
factions on supported maps. Match quality, performance and those scenarios remain unmeasured locally.

Research rationale and pinned primary sources: [RTS AI research](rts-ai-research.md#faction-identity-tunnels-and-air-operations).
