# Ground vehicle service

Optional `foundation.serviceControl` addresses free vehicles that are healthy but need ammunition, as
well as damaged vehicles. The shipped AI enables it. Null keeps the earlier HP-only specialist path.
This increment is compile-only locally under owner instruction, with behavior/cost evidence deferred.

## Readiness and ownership

An eligible crewed ground vehicle needs service when HP is below 80%, or an active serviceable weapon
pool is below 65%. Disabled secondary weapons, zero-capacity/unlimited pools and depot-only weapons
marked noService do not create an ammunition request. Garage service can refill ordinary no-resupply
or truck-reloaded weapons when the engine allows it; it is separate from aura supplier restrictions.
Smoke/APS deficits alone do not trigger a trip, though real service may refill them.

Before strategic recruitment, reserve up to two free individual combat vehicles for real reachable
service jobs. Existing mission, squad, specialist, cargo, pending-command and rest ownership stays
intact. During Defend posture, armed vehicles above the retreat HP threshold remain available to the
strategic defender allocation. Exhausted/critically unhealthy free vehicles can seek useful care.
The ordinary specialist pass can also assign unreserved eligible vehicles, including damaged cargo
trucks, after strategic allocation. The priority assignment cap applies to the early reservation pass.

Vehicles already in an assault/defense/recovery mission retain that mission's lifecycle; this change
does not split forces to extract one tank or bypass recovery/rest clocks. Optional aircraft doctrines
and their service commands remain separate. A missing or inaccessible garage never creates a fake job.

## Choosing a garage

Shortlist up to 16 operational compatible garages within the supply-trip limit, nearest then ID.
Refuse new assignments when eight other physical/planned vehicles already claim that garage. Count a
vehicle once even when both a planner job and a physical service reservation exist. Confirm known
safety and reachability to an actual door exit, rather than the blocked building center.

Among confirmed candidates, cost combines travel, known danger and other vehicle/production pressure
per garage door. This is a soft queue heuristic, not an exact wait-time model; production can change
and some doors can be occupied while others remain usable. Existing jobs keep their garage to avoid
queue hopping, with the ordinary absolute specialist lifetime still authoritative.

For a garage drawing on finite airfield stock, estimate the complete reload debit (including shared
weapon pools once and smoke/APS), then repair after affordable reload in the engine's payment order.
Require useful requested care to be affordable now. An ordinary garage without a stock-backed airfield
retains the engine's current free-service semantics. Quotes reserve no stock, do not assume cargo
arrivals, and are checked before issuing service, not after an in-progress service has already paid.
The real engine remains authoritative if stock or other work changes before entering a bay.

## Completing and cancelling

Issue the normal delayed Service command and retain the specialist reservation during approach,
waiting, bay work and exit. A vehicle that emerges still needing requested care cancels and cools down
instead of endlessly reissuing the same failed service. Garage loss/threat and absolute expiry cancel
through normal Stop when commandable. Cancellation retains ownership through input delay plus one
before release/rest. An actor inside a bay or driving out retains ownership while the engine completes
its safe exit; this can outlast the planning deadline. A dead owned actor releases through the usual
specialist reconciliation. Budget-delayed Stop retains ownership rather than allowing competing work.

Format 11 already stores specialist kind/target, stage, command and retry clock, lifetime and cooldowns.
Stage 3 is the service cancellation window; no additional retained memory is needed. Strict validation
checks the service stage/command/target shape and all seven policy fields enter the compatibility digest.
An older policy digest is rejected; the checkpoint format stays 11. The host still restores matching
World and pending inputs. Capability reports and existing specialist diagnostics expose these jobs.

## Limits and acceptance

Candidate/queue/quote scans still scale with owned roster size; the per-actor shortlist does not cap the
number of depleted actors examined. A better garage can lie outside the shortlist. Stock/queue quotes
are estimates, and engine changes to payment/pool semantics require reviewing the planner estimator.
No progress-specific service timer or optimal multi-garage scheduling is asserted; the existing specialist
lifetime bounds planning, while physical engine exit may take longer. Temporarily blocked/unfunded jobs
can be retried after cooldown. Infantry, smoke/APS-only servicing and extraction of assigned forces are
outside this increment.

Deferred: healthy empty vehicles before recruitment, active/disabled/noService pools, compatible tags,
queue pressure and production doors, stock-backed partial/failed care, garage loss during approach/bay/
exit, delayed Service/Stop and budget exhaustion, emergency defense preservation, exact format 11
continuation and representative roster cost. No local tests, matches or benchmarks were run.
