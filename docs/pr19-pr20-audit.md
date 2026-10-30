# PR 19 and PR 20 audit — 2026-10-30

Scope: audit the player AI foundation in PR 19 and the stacked strategic-control changes in PR 20, implement corrections, and validate them. Work stopped at the owner's request after committing and pushing the completed changes and this report.

Reviewed starting revisions:

- PR 19, `codex/player-ai`: `914d1ddac7bb72b03b4bb3709cf9dfb08e3024a1`.
- PR 20, `codex/ai-strategic-control`: `1cc242dde43cc243c9daff73da54c1d2bebc137a`.

Validated code revisions before this report-only addition:

- PR 19: `c9e140b6a056e2f08ad7f1eba0f194f1473372be`.
- PR 20, including all parent fixes: `19f9e88b498b7b20feba07d124c5c55e8fe45100`.

## Findings and changes

| Priority | PR | Finding | Implemented correction |
| --- | --- | --- | --- |
| P1 | 19 | Transport expiration could release waiting passengers while delayed Enter commands were still pending. Partial boarding also dropped ownership before cancellation completed. | Stop carrier and unboarded passengers together in one command-budget slot; keep reservations through input execution and disembarkation. Abort an incomplete group at the boarding deadline. |
| P2 | 19 | Repair, escort, artillery and aircraft jobs could release their actor while persistent movement, guard, fire or air orders remained active. | Send Stop or ReturnToBase before releasing these jobs, retaining the job when command emission cannot proceed. |
| P2 | 19 | An unchanged force order was suppressed when existing members were busy, leaving newly recruited idle or already-guarding units without the force objective. | Invalidate the cached last order whenever recruitment grows a force. Preserve ordinary suppression for unchanged membership. |
| P2 | 19 | Cargo trips did not reserve their truck against specialist assignment. A damaged truck could receive a service assignment while a delivery command was still in flight. | Include active cargo trucks in shared ownership checks and checkpoint overlap validation. |
| P2 | 19 | Garage service checked reachability at the blocked building center, outside the default nearest-passable search radius for large garages. | Test access at the garage's production door exits. |
| P1 | 20 | Fallback support fire bypassed the posture's distant-strike prohibition even when the specialist planner rejected the same offensive target. | Apply the same target permission check to fallback support fire. |
| P1 | 20 | Revoking an insertion could wait behind a specialist's future retry clock. The prior strategy cancellation also omitted stopping the carrier itself. | Enter the shared transport cancellation path before the retry gate, stopping carrier and waiting passengers together. |
| P2 | 20 | A growing defense mission could leave reinforcements guarding elsewhere because its cached home order remained unchanged. | Invalidate the cached order after defender recruitment. |

PR 19's conflict with current main was resolved by merging main into its branch. Both sets of design decisions were preserved; player AI decisions are now 770–791, with strategic-control decisions 792–798 in PR 20. Main's existing engine/content changes were inherited by the merge; the audit corrections themselves are confined to AI code, tests and documentation. PR 20 retains PR 19 as its base and contains the parent corrections.

## Validation completed

- PR 19 full solution suite: **2,785 passed, 0 failed, 0 skipped**.
- PR 20 full solution suite: **2,792 passed, 0 failed, 0 skipped**.
- Combined solution build: **0 warnings, 0 errors**, including library targets net8.0/net10.0, SimRun and tests.
- Whitespace checks passed for both branches.
- Foundation regressions cover pending and partial transport cancellation, specialist cleanup, idle/already-guarding reinforcement orders, cargo ownership and garage access.
- Fog isolation now runs with both the legacy ground policy and the foundation enabled; hidden enemy changes do not alter commands, missions, intelligence or situation in the fixture.
- A JSON checkpoint/replay fixture compares every subsequent command, world hash and serialized planner state with uninterrupted execution, including pending input commands. It runs on both branches, using shipped posture profiles on PR 20.
- Strategy regressions cover allowed/prohibited distant fire, insertion cancellation with a future retry clock, defensive reinforcements, weighted production choice, and whole-squad emergency recall despite posture hold and assessment interval.
- Command-line checkpoint/resume smoke check on PR 20: checkpoint at tick 241, resume to tick 480, compare with continuous execution. Both produced **10 commands** and world hash **`f52527915cdbc9a9`**; planner summaries also matched after excluding diagnostic trace history, which checkpoints intentionally omit.

Commands used:

```sh
dotnet test SimCore.sln --verbosity quiet -p:UseSharedCompilation=false -nodeReuse:false
dotnet build SimCore.sln --no-restore --verbosity quiet -p:UseSharedCompilation=false -nodeReuse:false
git diff --check
```

The new fixtures are in `tests/SimCore.Tests/PlayerAiFoundationTests.cs` and, on PR 20, `tests/SimCore.Tests/PlayerAiStrategyTests.cs`. The existing fog fixture was extended in `PlayerAiTests.cs`. Regression failures were reproduced before the corresponding fixes; positive controls also check permitted fire and both production priority directions.

## Limits and handoff

The pre-audit PR heads had passing Linux/macOS/Windows checks. Fresh remote CI for the pushed audit revisions has not been reviewed; local results above do not claim fresh cross-platform success. Neither PR was merged.

This audit does not establish win rate, balance, large-map performance or exhaustive specialist/terrain behavior. Existing limitations remain, including safe helicopter abort landing-zone selection, fixed-wing vehicle airlift, first-unplaceable-goal construction blocking, and engine cash hooks that remain no-ops. Continue representative-match testing before treating the planner as a tuned opponent.

All audit commits and pushes use personal account `yasserreslan`. The active `gh` account was not switched from `yasser-reslan`. Work was done in separate audit worktrees; the original main checkout and its unrelated image were left intact.
