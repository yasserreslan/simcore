# Handoff: engine notes for a new session (2026-10-17)

simcore is the deterministic lockstep simulation for a Generals-style RTS. It's
public (MIT). The game client, art and design work live in a separate private
repository that uses this one as a submodule at `external/simcore`. If you have
that repository, read its `docs/handoff.md` first; it covers the whole project.
This file covers the engine only.

## Rules that never bend

- **Determinism:**
  - integer fixed point (1 cell = 1024 units), 20 ticks per second;
  - no floats, threads, wall clock, Dictionary/HashSet iteration or
    GetHashCode in the sim;
  - every sort needs a total order (IDs break ties).
- **The golden replay** (`tests/.../ReplayTests`, `BotMatchMatchesGoldenCommands`):
  - performance-only changes must leave it byte-identical;
  - gameplay or content changes re-record it **deliberately**, with the reason
    in the commit message.
- **CI:** it runs the full suite on Linux, macOS and Windows; keep it green.
- **Content:** content data lives in `content/` (`mvp`, `setups`, `terrain`).
  Tunable numbers belong in content, not code.
- **Hashing:** new hashed state goes into `World.Hash`. Caches that are pure
  functions of hashed state stay out of it (the spatial index, the routing
  caches, the wall distances).

## What the engine has (main `2e0724a`)

- **Core:** units, weapons with two-slot loadouts, warheads with per-armour
  percentages (plus `maxHpPercent` for building damage), splash (including
  two-ring), guided missiles (homing, max flight, operator line), ballistic arcs
  that meet the terrain, salvos, deploy/stow, crews and garrisons.
- **Scale:**
  - a spatial index for vision, targeting and projectiles;
  - HPA* routing with road highways, corridor flow fields, a request queue and
    formation slots;
  - a MovementSystem with avoidance, collision, turn rates, clearance,
    slope and forest speed, arrival and stuck detection.
  - The 10,000-unit bench is at about 22–24 ms tick p99.
- **Terrain:** an optional heightfield (corner heights), slope classes per
  movement class, roads, forest density, pads, line of fire, crest shielding,
  and the high-ground range bonus. Flat maps keep the old behaviour and hash.
- **Economy pieces:** supply depots with finite stock, depot reload costs per
  weapon, and cargo trucks.
- **Building and producing:** construction (sites, stages, workers), and the
  vehicle garage (a production queue, 2 bays, 4 doors with a seeded random
  door, rally points).
- **Drones and air defence:**
  - suicide drones: a launch, a smooth climb, cruise, and a pitch-over into a
    dive;
  - unit stances: Guard, Hold fire, Return fire;
  - air defence: lock, a lead-pursuit missile, a proximity fuse, and a seeded
    kill roll.
- **Tools:** `SimRun` with `-bench`, `-move-bench`, `-routing-bench` and
  `-terrain-bench`, plus headless match runs.

## Working practices

- **Branches and history:** work on a branch or worktree when another agent
  shares the checkout. Use fast-forward pushes and never force-push.
- **Commands:** append new `CommandKind` values only (`CommandKindValuesAreStableAndUnique`
  pins them).
- **Tests:** add tests with every system: determinism (run twice, reverse
  order), the rules, and edge cases.
- **Benchmarks:** keep the benchmarks runnable headless; graphical stress
  tests belong to the client.

## Next engine milestones (planned in the game repository)

- Vision, fog of war and forest concealment (per-team visibility grids,
  integer shadowcasting).
- Air-defence detection by radar or spotting.
- An economy (credits) behind the existing no-op charge and refund hooks.
- Deterministic multithreading, only if the benchmarks demand it.
