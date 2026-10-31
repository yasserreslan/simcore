# Builder survival and construction safety

Optional `foundation.buildPlanning.safety` protects the ground construction loop from losing workers
while they continue building under observed fire. It is enabled in the shipped AI policy. Null retains
the preceding placement threat rule and no evacuation pass. This is compile-only locally under owner
instruction; actual survival, construction continuation and cost require acceptance evidence.

## Threat checks and evacuation

Before strategic/specialist assignments, consider unreserved mobile individual builders that are idle,
building or have a delayed Build/Resume input. Check up to 16 nearest visible armed opponents, with
stable ID ties. Compatible stocked active weapons within potential reach plus four cells trigger
preservation. AA-only weapons and genuinely unarmed observers do not automatically threaten a worker.
No remembered enemy ID is resolved into a live enemy; hidden death, movement and ammunition do not
enter the decision. Potential reach does not prove line of fire or predict missiles in flight.

Up to two workers receive preservation commands per decision, lowest HP percentage then ID. Existing
mission/squad/specialist ownership is respected. Candidates include the ordinary home estimate, defended
core, nearby own built structures and eight local escape offsets. Only observed points are considered;
duplicates are removed with a list. Shortlist at most 12 by known danger/distance/cell, confirm terrain
access and recheck the actual passable endpoint's visibility and compatible weapon reach.

Prefer the refuge with least maximum known danger at the endpoint and four intermediate straight-line
samples, then shortest distance and stable cell coordinates. Issue normal Move. If no observed reachable
endpoint avoids the checked weapons, issue Stop to interrupt construction rather than inventing a safe
refuge. Neither result guarantees survival. Straight samples are not the route the engine will take,
can cross obstacles, and cannot anticipate unseen attackers or account for all visible projectiles.

## Keep work paused and resume legally

A successful preservation command puts the worker on the existing rest cooldown for 200 ticks, at least
input delay plus one. The newer normal command supersedes delayed Build/Resume intent; pending records
remain until their ordinary expiry. Do not delete a partly built site. Ordinary site progress, retries
and abandonment still apply. A long unsafe interval may ultimately cancel an abandoned site under the
existing build policy; the goal can later seek another location.

Stalled-site reassignment now respects rest and other planner ownership even for a previously assigned
worker. New and resumed building approaches must pass the known-danger ceiling and compatible visible
weapon check. Resume uses the passable approach near the site's edge. New placement uses its resolved
passable candidate point, which is an approximation of the worker's eventual construction position.
Under this optional policy, mere proximity of an unarmed scout no longer vetoes every new placement;
actual threat compatibility and known influence do. Normal CanPlace/vision/terrain checks still apply.

Workers in evacuation cooldown are not retargeted every decision; a new danger can arise before the
cooldown ends. Once rest expires, construction still needs a free worker and a safe approach. This does
not release transport passengers, take over specialist jobs, fight with workers, provide healing, use
instant movement or put workers in hypothetical garrisons. The refuge pass can retain a worker in place
with Stop when no better checked endpoint exists.

## State, costs and acceptance

Format 11 remains sufficient: the decision uses existing owned orders, delayed inputs and rest deadlines.
There is no new escape mission or retained enemy reference. All eight safety fields are validated and
included in the policy digest; an old policy checkpoint is rejected. Capability diagnostics expose whether
builders are available. Shared simulation state/hash, gameplay data, goldens and private clients stay intact.

The worker/visible-roster scans and point ranking scale with input. Weapon/refuge/route work per considered
worker is bounded by policy, while the move cap does not cap all unsuccessful searches. A bounded nearest
threat list can omit a dangerous distant weapon. Cell/range/route approximations can reject useful work
or miss a hazardous approach. Protected troops do not escort builders as part of this increment.

Deferred: last-worker attack during build and delayed placement; unarmed/AA-only observers; long-range
weapons and walls; refuge/access failure; cooldown versus stalled-site retry; site abandonment and safe
resume; multiple workers and exhausted command budget; hidden-state invariance and exact format 11
continuation; representative construction/roster cost. No local tests, matches or benchmarks were run.
