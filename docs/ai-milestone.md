# M1: Sustainable Standard Opponent

Status: **implementation in progress; not promoted as a validated standard opponent**.

Owner requirement: battles should have slight variety rather than repeating a fixed script. Fresh AI seeds
should vary openings, composition, timing and approaches while retaining reliable replay/lockstep behavior.

The milestone is a dependable ground-player foundation for the mechanics SimCore actually exposes.
On a supported lobby, it should scout, build, produce, defend, attack, recover and repeat without
cheating, losing ownership of units or waiting forever on its own plans. More features alone do not
complete the milestone. Aircraft and transport executors already exist, but unsupported optional
content must not prevent the basic ground loop.

## Standard behavior and acceptance

| ID | The opponent should… | Code today | Remaining acceptance evidence |
| --- | --- | --- | --- |
| M1-01 | Use owned state, current visibility and explicit observation memory; issue normal delayed commands | Observation boundary, frozen contacts, ordinary command API | Parent fog/replay audit exists; recheck new paths and platform behavior |
| M1-02 | Explore using suitable free units; retain/revisit sightings | Ground information/refresh/danger utility and scout preservation; air probes; healthy unarmed observers | Armed/unarmed scouts finish, defer risky/inaccessible probes, preserve visibility and recover from visible weapon threats without loops |
| M1-03 | Build a useful base without one impossible goal blocking the queue | Per-goal searches/backoff, site progress/retry/abandonment, builder preservation | Blocked-first-goal fairness, builder loss and passive airfield cases |
| M1-04 | Produce replacements and respond to observed threats using real producers | Capped desired counts, counter goals, compatible facilities/stations and missing-producer priority | Destroy/rebuild a producer and resume production; disclose missing training chains |
| M1-05 | Defend home promptly, then release spare forces for an offensive | Emergency posture, reserves, recall; squad-safe surplus redistribution added | No repeated recall/release oscillation; no splitting a squad or permanently retaining all healthy forces in safe Attack posture |
| M1-06 | Reinforce useful existing forces before repeatedly creating undersized new ones | Bounded local reinforcement of safe active/staging assaults added | Whole groups join once, receive the objective and preserve loss/progress bookkeeping |
| M1-07 | Maintain persistent attacks, choose legal local targets, regroup and retreat | Readiness-gated operations, bounded gather/concentration and shared progress/losses; focus fire/pressure sharing, complete local matchup confirmation, bounded rallies, danger-aware recovery | Known reachable objective produces an offensive commitment within configured assembly/staging/update budgets |
| M1-08 | Keep usable forces supplied and serviced | Finite-stock cargo, danger-aware providers, capped repairers, moving operation support, garages and aircraft service | Repeated supply/service cycles with depleted stores and unavailable providers |
| M1-09 | Bound and clean up every planner-owned assignment | Mission/cargo/specialist deadlines; persistent crew reservations and cancellation added | Pending boarding, failed crewing, target loss and command-budget exhaustion release safely |
| M1-10 | Continue exactly from a matching host checkpoint/replay | Versioned planner capture/restore, policy compatibility digest | Parent continuation audit exists; validate format 9 operation state/target updates, seed identity, force/crew and local-combat/recovery clocks, tunnel phases and specialist operation ownership |
| M1-11 | Explain what it is doing and why it cannot proceed | Situation/directive, traces, missions/jobs/build diagnostics and capability blockers | Host/UI can distinguish unsupported content, unavailable prerequisites, cooldown and active work |
| M1-12 | Be tunable without engine edits or platform-dependent decisions | Strict policy, integer comparisons, ID ties, count/tick budgets | Review policy bounds, platform CI and representative match behavior |
| M1-13 | Produce varied battles through small, explainable strategic choices | Fresh host AI seed, weighted battle styles, bounded timing/mix/objective/flank variation | Different-seed diversity; same match/AI seed + policy/input reproducibility; resume keeps seed and style |
| M1-14 | Give each faction a distinct, roster-grounded playstyle | Data-driven doctrines: Resistance raiding/tunnel lifecycle; Sentinel armor/air readiness, cover and servicing | Supported-faction matches, tunnel collapse/capacity/evacuation, repeat sorties/threat changes, fog and format 9 continuation |

The code column means implemented behavior, **not a claim that each scenario passes**. The current
owner instruction is no local tests or matches: compile and review code now; leave behavioral and
performance gates explicitly open. Existing green CI covers the suite it runs, not every acceptance
scenario above.

## Scope and promotion gate

Promotion requires an owner-reviewed supported lobby with a documented roster and legal production,
construction and resupply chains. Then gather evidence that:

1. The full ground loop repeats after losses, rather than stopping after the initial wave.
2. A visible home threat interrupts normal planning within a decision interval and commands are
   delivered within the normal input budget/delay; an ended emergency does not trap all forces at home.
3. Stalled placement, orders and jobs have bounded retries/cancellation and do not duplicate ownership.
4. Hidden enemy changes do not affect decisions, and checkpoint continuation preserves command order/state.
5. The opponent completes representative matches on flat and terrain maps without planner deadlocks.
6. Think cost and retained state are measured at representative scale before a client performance claim.

Variety is a promotion criterion too: compare several AI seeds on the same setup, verify meaningful
opening/attack differences, and verify exact regeneration with the same seeds/policy/inputs. Randomness
must not weaken legality, grant resources, delay emergency response or change the shared simulation RNG.

No fixed win-rate target is asserted yet: establish a repeatable baseline against the old opponent,
then choose a target. Do not mark this milestone complete merely because every module exists or the
solution compiles. Do not run the deferred local scenarios under the current no-local-testing instruction.

## Content dependencies must be visible

`PlayerAi.Capabilities` reports configured policy versus currently available friendly prerequisites.
It identifies missing construction workers, operational producers, builder/infantry replacement goals,
stocked ammunition suppliers, aircraft chains and local reinforcement opportunities. It is diagnostic
only and never feeds decisions. The report uses the last observed friendly roster, not hidden enemies;
it does not prove reachability or queue availability.

Starting workers/infantry are a finite resource when their production chains are absent. Cash hooks
remain engine no-ops. Neither issue should be hidden by sandbox spawning, free refills, imaginary
credits or hardcoded faction privileges. Add missing game content as a separate explicitly reviewed
engine/content task if needed.

## This increment

- Whole squads remain intact during defense-to-assault redistribution; loose units may move separately.
- In safe Attack posture with a known structure objective, the reserve can shrink enough to retain one
  minimum-sized or whole-squad offensive. A visible home intruder removes that exception immediately.
- Every 100 ticks, or sooner when newly ready units appear, safe active/staging assaults can receive ready whole groups within 20 cells. At most
  8 groups transfer per decision, with defense rebalancing first; each recipient examines at most 32
  nearby candidates before reachability checks. Reinforcement does not reset a stalled mission's clock.
- Full-sized forces may launch without optional role preferences; essential roles still gate launch.
- Eligible unarmed observers can scout and do not immediately retreat merely for lacking ammunition.
- Up to 16 persistent crew jobs reserve drivers for at most 600 ticks; failed boarding retries or cancels
  through normal commands. Cancellation waits at least input delay before permitting reassignment.
- Optional Reconnaissance adds information-value/refresh scoring and scout preservation; see [algorithms and limits](ai-reconnaissance.md).
- Optional CombatControl adds local confirmed withdrawal, target pressure sharing, scored recovery providers, moving support and safer transport aborts; see [mechanics and limits](ai-combat-control.md).
- Optional OperationControl adds readiness/strength release, safer approaches and bounded shared commitment; see [algorithms and limits](ai-operation-control.md).
- Format 9 captures operation commitment plus local combat/recovery state as well as tunnel trip phases/retries and specialist operation associations, plus battle seed identity, the reinforcement timer and crew jobs. Null forceManagement preserves the previous
  force planner; the shipped policy enables it.

Faction mechanics and exact limits: [faction doctrines](ai-faction-doctrines.md). M1-14 is implemented but
awaits behavior evidence alongside the existing criteria.

## Next work, in order

1. Review/tune the new operation support and recovery heuristics against supported scenarios when validation is requested.
2. Improve resupply/retreat destinations when stock, access or threat changes.
3. Add the outstanding acceptance fixtures and representative-match evidence when validation is requested.
4. Tune composition, timing and difficulty from that evidence; avoid adding unrelated special tactics first.

Navigation: [rules/integration](player-ai.md), [source research](rts-ai-research.md),
[parent audit](pr19-pr20-audit.md).
