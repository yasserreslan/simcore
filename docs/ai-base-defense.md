# Asset-aware base defense

Optional `foundation.defenseControl` replaces the shared nearest-intruder destination with bounded
protected-asset threat groups and whole-force allocation. It takes effect with the existing posture profiles; an empty profile list disables this layer.
This is code groundwork: compilation only locally, no tests, matches or benchmarks under owner instruction.

## Observe what threatens the core

The oldest surviving built production structure anchors the defended core; a supplier or the ordinary
home estimate is the fallback. This keeps a new forward tunnel from becoming the guard destination
merely because it shifts the average of all buildings. The default core radius is 64 cells.

Up to 16 valuable friendly structures/builders are protected. Production, workers and supply receive
policy bonuses in addition to content cost. The observation scan first shortlists up to 32 visible ground
contacts near those assets, then checks compatible stocked enemy weapons. An unarmed scout or an AA-only
weapon near a worker is not automatically a ground emergency. Explicit visible targeting increases risk.
A visible attacker explicitly targeting an asset can also qualify beyond the proximity radius when a
stocked compatible weapon potentially reaches it, within the travel cap; a distant rifleman does not
qualify merely because it was ordered to attack a building. No remembered enemy ID is resolved through World.Unit to discover current position, death or weapons.

At most eight groups are built from those contacts, using a 12-cell fixed seed radius rather than
unbounded neighbor chaining. Group centers, potential strength, threatened assets and risk priorities
come only from the current observation. Candidate/group overflow is deliberately omitted, not treated
as evidence that the whole battlefield is covered. Cheap visible/asset scanning still scales with input
size, including cheap weapon flags/ranges. Full weapon selection and grouping work are bounded by policy counts.

Air-to-ground pressure near the protected assets is observed separately with the same bounded contact
limit. It can trigger emergency posture/cover, while ground defenders keep ground destinations. Pure
fighters without a compatible ground weapon do not generate that pressure. Interception remains an
ordinary engine/air-specialist responsibility; this is not missile-flight prediction or radar omniscience.

## Allocate complete forces to actual needs

Existing defender missions are considered by strength, then ID. Each threat has a desired coverage
ratio (125% of its heuristic strength by default). Greedy assignment prefers still-uncovered compatible
threats, then balances asset risk, armor-adjusted potential fire and travel. Positive fire utility is
bounded by risk value; covered targets cannot win merely by offering a huge weapon score. Terrain access
must work for every member, within 200 cells of the lead. This layer does not initiate defense tunnel trips.

A five-second assignment hold limits churn while its current contact group remains reachable and useful.
A threat above twice its priority can override the hold, and already covered assignments do not hold a
redundant force back from other needs. A continuing group can retain identity even when its highest-risk
member changes. Current targeting can return fire against an attacker outside its assigned group.

When several raids need separate responses, reserve redistribution can move whole squads or loose-unit
groups into separate defense missions. It never divides a squad, including oversized existing squads up
to the engine's 64-member bound. Transfer and candidate limits apply, together with force-management's
shared transfer limit. Current combat does not itself forbid an emergency defense-to-defense transfer.
If a reserve cannot be divided, free ready groups may provide additional coverage, up to four separately
allocated defense missions and the existing mission cap. Insufficient forces remain insufficient.

Those four missions bound expansion/splitting, not an unsafe forced merge of already existing defense
missions. A whole oversized squad may only cover one raid; no coverage guarantee follows from the desired
count. When no useful threat is assignable, forces guard reachable high-value assets, preferring different
assets across guard missions. Guarding a worker can follow that worker's current position.

## Keep the reservation honest

The defender reservation counts armed, available members of non-transit missions rather than claiming
that an underground/evacuating force is already defending home. Completely empty-ammunition or critically
unhealthy defense forces enter the ordinary recovery lifecycle; healthy replacements can be allocated.
A partial force's empty members do not satisfy the armed reservation. Whole-force recovery still respects
member ownership, deadlines, real suppliers and command budgets.

During an observed ground/air emergency, surplus mission release is deferred so a newly allocated second
response is not immediately dismantled by the percentage reservation. Once the emergency ends, the shared
strategic/force-management layers can again release surplus groups for attacks. This is a heuristic
readiness/coverage policy, not an optimal multi-agent assignment or combat outcome model.

Ready fighter cover whose ground operation ended during a defense recall can retask directly to the
protected core. Fuel/health/ammunition readiness remains mandatory. Other aircraft still use normal
cancellation/rearm behavior; the planner never manufactures a sortie or free station.

## Retained state and deferred acceptance

`DefenseThreats`, `DefenseAnchor`, `AirDefensePressure`, mission assignment views and runner traces explain
what was observed and assigned. Threat/asset lists and coverage arrays are current-decision scratch, not
persisted enemy references. Checkpoint format **10** retains only per-mission threat/asset identity and
assignment hold, alongside existing operation/recovery/tunnel state. Restore validates policy, kind and
clock/ID shape; next Think rebuilds observations. Formats 1–9 are rejected. Every defense policy field is
digested. Null defense control retains the previous defense allocation path.

Deferred scenarios: simultaneous raids, bait versus production/worker threats, limited/unreachable or
unsplittable forces, changing armor compatibility, hold/override/coverage behavior, asset/anchor loss,
posture recall/release, pending inputs, depleted/ammunition-seeking defenders, tunnel evacuation,
air-to-ground pressure and cover retasking, fog-hidden changes, exact format 10 continuation and large
visible/owned rosters. Multi-base protection and optimal coverage remain future work; measure and tune
representative supported maps before claiming defense quality.

Research context: [bounded defense allocation](rts-ai-research.md#bounded-defense-allocation).
