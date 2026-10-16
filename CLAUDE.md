# CLAUDE.md: simcore

Instructions for any Claude agent working in this repo. Read this first.

## What this is

The deterministic simulation core of a classic-style RTS: the whole game minus
graphics, sound and input. A renderer reads the world and draws it and never changes
it. A private client consumes this engine as a git submodule and pins a specific
commit, so every change here must keep the public API and the content format stable
unless the task says otherwise. See `README.md` (layout, content model, rules) and
`docs/design.md` (the game design).

## Build and test

```sh
dotnet build SimCore.sln
dotnet test                                   # all tests, including the 10-minute golden replay (slow: run it once, not in a loop)
dotnet test --filter GoldenReplay             # just the golden replay
dotnet run --project src/SimRun               # bot vs bot, text map
dotnet run --project src/SimRun -- -every 20 -ticks 2400
```

`TreatWarningsAsErrors` is on. The library targets `net8.0` and `net10.0`; SimRun and
the tests target `net10.0`. Run one heavy `dotnet test` at a time (the machine can
overheat); never leave processes running.

## Determinism rules (hard)

Code in `FixedPoint`, `Rng`, `Model`, `Sim`, `Scenarios` and `Bots` must follow these;
`DeterminismRulesTests` enforces the first five and fails on any new source directory
that is not exempted on purpose (`Content` and `Replays` do IO and never run inside a
tick).

1. No floating point (`float`, `double`, `decimal`, float math). Use `Fixed`.
2. No hash collections (`Dictionary`, `HashSet`, `GetHashCode`, LINQ built on them:
   `Distinct`, `GroupBy`, ...). Use lists sorted by ID.
3. No time, environment or global randomness (`DateTime`, `Stopwatch`, `Environment`,
   `System.Random`). Use the world's `Rand`.
4. No threads (`Thread`, `Task`, `Parallel`, `async`).
5. No `unsafe` or reflection.
6. Fixed order everywhere: systems run in a fixed order and visit units by ID; every
   sort or "pick the best" comparison breaks ties by ID.
7. Everything that matters is hashed: new state goes into `World.Hash`.

Integer arithmetic wraps; `/` and `%` truncate toward zero; `>>` on signed values is
arithmetic. Use tags and requirements in content, never "is this an Assault?" checks.

## Content is data (`content/mvp`)

Every unit, component, warhead and faction is JSON under `content/mvp`; lobby files are
in `content/setups`. Adding content is adding data; a new mechanic is one new behavior
(system) plus data. Distances are in tenths of a cell, speeds in tenths of a cell per
second, times in ticks (20 per second), percents 0 to 100. Unknown fields are errors;
comments are allowed. The content hash covers the compiled definitions, so layout and
whitespace do not change it, but any value change does and invalidates the golden replay.

**Data-driven tuning.** Gameplay numbers (HP, speed, range, damage, reload, scatter,
splash, capacities, building stages, ...) belong in content, never in code constants.
A tuning change is a data change plus a golden re-record.

## Golden replay

`tests/SimCore.Tests/testdata/battle_seed42.json` (replay) and `.hashes` must reproduce
on every OS; CI runs it on Linux, macOS and Windows. After an **intentional** gameplay or
content change, re-record and review:

```sh
SIMCORE_UPDATE_GOLDEN=1 dotnet test --filter GoldenReplay
git diff --stat tests/SimCore.Tests/testdata   # expect changes only because of your change
dotnet test                                    # must pass without the variable
```

Commit the new golden files in the same commit as the change that caused them. Never
re-record to hide an unexplained desync: find the nondeterminism (a rule above) instead.
Related guards: `BotMatchMatchesGoldenCommands` (bots and RNG are exact),
`EncodeMatchesGoldenBytes`, `RefusesOtherContent`, `RngTests.KnownSequence`.

## CI expectations

`.github/workflows/ci.yml` runs `dotnet test SimCore.sln` on ubuntu, windows and macos
for every push and pull request, on .NET 10. It must stay green on all three. A
docs-only change (Markdown) cannot affect it but still triggers it: confirm it passes.
Do not add platform-specific behavior, locale-dependent formatting, or file-system
order dependence.

## Commit isolation

Other people or agents may be working in the same repository at the same time.

- Stage only the paths you changed (`git add <paths>`); never `git add -A` or
  `git commit -a`.
- Check `git diff --cached --stat` before every commit.
- Never touch, stash, reset or revert files you did not change.
- `git fetch origin`, then push only if `origin/main` is an ancestor of `HEAD`;
  otherwise rebase only when your tree is clean apart from your own commit, or retry
  later. Never force-push.
- One topic per commit; keep the public API and content format stable.

Keep the repo engine-only: no game art, no personal details, no credentials.
