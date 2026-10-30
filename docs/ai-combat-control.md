# Local combat judgment and recovery

Status: code foundation; no local tests, matches or benchmarks under the owner's instruction. These
algorithms need the behavioral evidence in [M1](ai-milestone.md) before a quality claim.

Optional `foundation.combatControl` connects local force decisions, target commitments, recovery and
support. Null preserves the earlier executor behavior. All decisions use integer arithmetic, ordinary
commands, stable IDs and owned/currently visible or explicitly remembered information. Faction doctrines
still determine force shape and battle style; this layer handles shared survival and support mechanics.

## Local combat estimate

An active assault periodically gathers visible ground opponents near any member and nearby friendly
participants. Mission members come before outside support. Nearest distance and ID break ties. The
shipped policy allows 24 participants per side and 1152 weapon-pair checks every 60 ticks. If either side
or the pair calculation exceeds its limit, the estimate is marked incomplete and cannot cause withdrawal.
The existing health, loss, stall and lifetime recovery rules remain active.

Each participant contributes its best armor-adjusted stocked compatible weapon score, scaled by current
health. External friendly support counts at 75%. A unit contributes once, so multiple weapons or multiple
targets cannot multiply its power. This estimates potential local fire; it does not simulate reload state,
projectile arrival, coaxial fire, exact firing positions, terrain, splash or all movement interactions.

Enemy power must exceed 175% of friendly power continuously across assessments for at least 120 ticks,
after a 100-tick active-assessment grace, before the assault recovers. A complete favorable/no-threat
assessment or an incomplete assessment clears confirmation. Defenders are not withdrawn by this rule.
Grace/confirmation prevent a single sample from reversing an offensive. This is a conservative heuristic,
not a proof that the force will win or that retreat is physically possible.

## Target pressure sharing

Visible tactical focus and aircraft target scoring include a soft penalty for other nearby missions and
armed specialist jobs already committed to the same target with a legal in-range weapon. Pressure is
compared with target strength; unarmed targets use health over the confirmation horizon. Above 180%
commitment, the penalty rises to a capped 2400. The current mission is excluded from its own penalty.

This distributes some attention across alternatives without splitting squads or making an already
committed target illegal. Existing focus, return-fire, objective and finishing bonuses remain. This is
not exact volley reservation or a guarantee against overkill; delayed commands and autonomous engine
fire can change actual damage. Only successful emitted mission intentions contribute retained state.

## Recovery destinations

Recovery candidates include compatible operational friendly suppliers, repair/service providers and
armed structures, plus home and the current position. Up to 16 provider destinations receive reachability
checks. Provider observation/scoring still scans the friendly roster. Ground destinations must share a
terrain component with every member; door/footprint access is approximated by nearest passable cells.

Endpoint danger is ranked first against force strength. Among equally safe/unsafe candidates, utility
combines distance, maximum known danger at the endpoint and four straight-route samples, and bonuses
for needed ammunition (below 65% weapon stocks) or compatible repairs. Known danger uses the existing
age-decayed observation memory. The samples are not a computed path and cannot establish route safety.
If no shared reachable destination exists, the planner retains the current position as an explicit fallback.

Returning forces reevaluate every 100 ticks. Loss of a provider, depleted stock, lost access or a newly
unsafe destination can change the objective. A still-valid destination requires 200 utility improvement
to switch. Replanning never extends the recovery deadline or resets progress. A safe place can therefore
be reached and released; this layer does not create ammunition, repair capacity or endless recovery jobs.

## Supporting the operation

- Radar and dedicated air-defense jobs associate with an active ground operation, follow its forces
  from an eight-cell rearward offset, and check friendly reachability and known danger. They reanchor
  when operations end rather than retaining an obsolete post forever.
- Repair assignment respects a one-repairer-per-patient cap and nearby friendly protection. Existing
  repair jobs cancel when the patient enters containment or its position becomes too exposed.
- Store selection weighs danger and route exposure as well as distance. Cargo jobs cancel and cool down
  when the destination becomes threatened; vehicle service approaches also respect support safety.
- Transport aborts cancel pending boarding before attempting a reachable safer ground destination or
  an air landing point passable for the retained passengers. Air travel samples use air threats; landing
  scores include ground danger. The engine still chooses the real legal unload locations.
- After the existing specialist deadline plus the configured recovery window, an abort falls back to
  local unload attempts. Passengers retain ownership until actually unloaded or lost; a failed exit
  cannot silently release contained passengers into another job.

## Checkpoints and evidence still needed

Format **8** retains local assessment/confirmation/grace clocks, power diagnostics, recovery clocks and
provider identity in each mission, alongside format 7's tunnel and specialist-operation state. Immutable
memory records are exposed through mission views. The policy digest covers every combat-control field.
Formats 1–7 are rejected instead of silently losing decisions needed for continuation.

Deferred scenarios: armor counters and empty ammunition; nearby support moving away; capped/partial
assessments; confirmation reset and exact resume; multiple forces choosing targets under command-budget
pressure; supplier depletion, destroyed providers, changed access and threat; unreachable home; support
withdrawal; transport abort before/after delayed boarding and unavailable landing cells. Measure large-roster
observation/candidate cost and tune ratios from supported matches when validation is requested.

Source rationale: [research follow-up](rts-ai-research.md#local-judgment-pressure-sharing-and-recovery).
