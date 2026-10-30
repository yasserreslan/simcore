# Reconnaissance value and scout preservation

Optional `foundation.reconnaissance` improves independent ground reconnaissance without changing the
simulation's visibility rules or creating free information. This is code groundwork; no local matches,
tests or performance runs were performed under the owner's current instruction.

## Where to look

The earlier ground executor picked the oldest available reachable probe, using travel as a tie-breaker.
The optional planner instead takes up to 128 oldest/nearest candidates and scores them by:

- Stale/unseen probes in a one-region neighborhood: a coarse information-gain estimate.
- Observation age capped at 2400 ticks, with unseen regions receiving that capped value.
- A bonus near an explicitly remembered contact not refreshed for at least 400 ticks.
- Travel cost, including the existing seeded scout preference, and age-decayed known danger.

Age, information, contact, travel and danger weights are policy values. Only 16 ranked endpoints receive
terrain-access checks per selection. Each member must share the resolved endpoint's movement component;
the endpoint is checked for danger again after passability adjustment. Oldest/nearest ties and score ties
are stable, using probe indices. No source-game exploration or path code is imported.

Known danger above the configured 200-point heuristic budget defers a probe for 600 ticks. Failed access
also defers it, so the next selection can examine other candidates rather than repeatedly spending the
same route budget. Deferral is not a successful observation. The planner writes the existing per-probe
retry state, while actual World visibility remains the only source of new sightings and seen timestamps.

The cheap available-probe scan still scales with map regions. Information/danger scoring and terrain
checks have separate count limits; this is not an incremental map-wide frontier solver or a fixed
per-frame cost guarantee. Region neighbors approximate information value, including after an endpoint
moves to a nearby passable cell. Buildings, line of sight and exact visible-cell gain are not simulated.

## Preserve what was learned

An active scout rechecks destination danger and up to 32 nearby currently visible armed opponents every
decision. A stocked compatible weapon within its potential range plus a five-cell buffer triggers normal
recovery. The check considers every compatible weapon, weapon mode, garrison/structure range origins
and range bonuses; a shorter high-DPS weapon cannot hide a longer-range threat.

This is deliberately conservative: it does not require exact line of fire or claim the opponent can fire
in its present deployment/flight state. No hidden unit ID is resolved to discover threats. The scout's
old probe receives at least the normal failure retry or 600 ticks, whichever is longer. Existing
threat-aware recovery chooses a destination when enabled; the prior home/supplier fallback still works
without that optional layer. Recovery deadlines, contained-unit ownership and command budgets remain
normal. Independent scouting seeks information rather than committing itself to a duel.

A retreat does not erase observations already made. Frozen contacts feed the shared strategy and both
faction doctrines. Resistance still allows more independent observers; Sentinel retains its ground
scout plus the separate bounded aircraft reconnaissance policy.

## Compatibility and deferred evidence

Null reconnaissance preserves previous probe selection and scout commitment. Policy validation and the
compatibility digest include all controls. No new retained state is needed: existing probes, seen/retry
arrays, mission objectives, recovery state and host commands capture every choice. Format 8 remains the
checkpoint format; an older policy digest is rejected rather than silently adopting new behavior.

Deferred evidence: fog-hidden changes must not change decisions; stale contact refresh and real visibility;
danger rejection without marking cells seen; mixed movement access and adjusted endpoints; multiweapon
range and ammunition checks; garrison/air threats; failed-probe backoff and candidate fairness; exact
continuation with deferred probes and scout recovery; multi-seed diversity and large-map candidate cost.

Research context: [ground commitment follow-up](rts-ai-research.md#reconnaissance-and-preservation).
