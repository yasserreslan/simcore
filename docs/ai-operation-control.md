# Coordinated operation commitment

Optional `foundation.operationControl` adds a shared readiness/progress controller above individual
assault missions. It complements faction doctrine, local combat judgment and bounded recovery. This
increment is compile-only locally: no tests, matches or benchmarks under the owner's instruction.

## Gather enough useful forces

The nominal force count still comes from the faction: Resistance usually raids with one force;
Sentinel prefers two. Strategic posture can lower that commitment. When known objective-area danger
exceeds assigned strength, up to two additional whole forces can join the same gathering operation,
within the current posture's assault cap and mission limit. This allows concentration rather than
repeatedly creating separate undersized attacks against the same defended objective.

A force is ready when at least 75% of its members are near its staging point, healthy and armed.
Contained/tunneling forces are not ready. Whole missions release together; membership is not split to
hit the percentage. Release strength counts only staged, healthy, armed participants and must reach
110% of the existing known-danger estimate. This is a heuristic comparison, not a prediction of victory.

The operation reviews readiness every 60 ticks. Enough ready forces and strength can commit early.
Otherwise the original gathering deadline may extend in 200-tick increments, at most 600 ticks beyond
its original time, and never beyond the absolute operation lifetime. At the final review, a smaller
viable ready group may commit; whole late/unready forces recover. If none is viable, the operation
withdraws. A visible change that compromises a staging point also causes withdrawal. Reviews and normal
input delay add their ordinary scheduling latency; no wall-clock timer is involved.

The strategic layer retains emergency priority. Full operation capacity causes normal strategic
recovery instead of an uncoordinated bypass attack. A failed safe approach is a recorded planning
failure. Neither condition spawns units, changes producers or bypasses terrain/content rules.

## Pick safer and separated approaches

The existing bounded approach ring still supplies candidates. Each must be reachable by every member,
either across the surface or through the doctrine's bounded compatible friendly tunnel planner. This
also fixes rejecting a valid cross-component tunnel staging destination merely because it lacks a
surface route.

Utility considers known endpoint danger, maximum exposure at four straight-route samples, distance
from the force and preferred approach, and a soft penalty for staging within eight cells of another
force in the same operation. Endpoint danger must remain within the configured force-strength ratio.
Stable candidate coordinates break score ties. If no candidate qualifies, the operation does not claim
that the current position is a successful safe forward approach.

The samples are not a terrain path; tunnels have their own time/capacity/exit checks. Nested tunnel and
approach budgets are bounded per attempt but unprofiled. Separation is a preference, not collision
reservation or a guarantee of flanking geometry.

## Track a commitment, not just independent orders

On release, the controller captures the member count, distance progress and currently visible target HP.
An advancing operation withdraws when:

- Its absolute deadline expires or the frozen contact is no longer known.
- Remaining committed members fall below 45% of the observed peak, including loss of a force to recall
  or its own recovery. Reinforcement can raise that peak; it cannot reset the operation deadline.
- There is no two-cell approach improvement or directly observed objective damage for 800 ticks.
  Merely issuing attacks or seeing any nearby opponent does not refresh this shared progress clock.

A live bounded tunnel boarding/transit phase keeps the shared stall clock alive while the trip is
inside its own deadline. Trip cancellation/evacuation and the absolute operation lifetime still apply;
this prevents treating legitimate underground travel as a stalled surface march.

If a no-longer-known objective location is actually visible and the subject is absent from current
observations, completion records an observed-empty success, not a claim of hidden death. Surface forces
stop/release through the normal command budget, cancelling retained movement/attack intent even when
the actual unit order is still idle because input is delayed; tunnel forces record the observation then evacuate/recover
without releasing contained members. That observed outcome survives a delayed completion command.

Objective motion is read only from explicit frozen observations. A displacement beyond six cells can
update the target and bounded staging approaches, at most three times. Active tunnel trips defer that
update until the ownership lifecycle finishes. Lost access, no safe restaging point or a target that
keeps moving beyond the update budget withdraws the operation. No hidden enemy ID is resolved. Retargeting
never extends the absolute deadline; air and ground support see the shared updated objective normally.

## Host state and acceptance

`Operations` exposes immutable `AiOperationControlMemory`: review/gather/commit/progress clocks, best
distance, peak members, observed target HP, retarget count, retained observed-empty outcome and a reason. Checkpoint format **9** captures
that record and the current objective. Restore validates policy presence, clock/count bounds and reason
shape before mutating planner state. All operation controls are included in the policy digest. Formats
1–8 are rejected. Null operation control retains the earlier operation release behavior.

Deferred scenarios: delayed/stalled/underground forces, emergency recall while gathering, multi-class
and tunnel-only approaches, changing stage danger, concentration under posture caps, viable partial
release versus failed preparation, reinforcement/loss accounting, moving or hidden targets, observed
HP progress, approach samples/separation, exact checkpoints in each phase and budget-exhausted command
emission. Representative-match quality and nested planning cost remain unmeasured locally.

Research: [preparation and commitment follow-up](rts-ai-research.md#preparation-and-shared-commitment).
