# simcore

The deterministic simulation core of a classic-style RTS: the whole game
minus graphics, sound and input. A renderer (Godot 4 .NET or similar) reads
the world and draws it; it never changes it.

Every machine in a match runs its own copy of the sim. Players exchange only
commands, scheduled for a future tick. Same starting world + same commands =
bit-identical world on every tick, on every OS and CPU. That's what makes
lockstep multiplayer and replays work.

The core was ported bit-exactly from Go to C#; since then the sim runs from
data (see [Content model](#content-model)) and the golden replay is a
Phase 1 battle recorded by the C# sim.

## Run it

Run these from the repository root:

```bash
dotnet test                                                # all tests, including the 10-minute replay gate
dotnet run --project src/SimRun                            # bot vs bot, prints the map each minute
dotnet run --project src/SimRun -- -every 20 -ticks 2400   # watch every second for 2 minutes
dotnet run --project src/SimRun -- -save m.json            # save the replay
dotnet run --project src/SimRun -- -replay m.json          # play it back
```

SimRun also takes `-seed N`, `-content DIR` (default `content/mvp`) and
`-setup FILE` (the lobby file, default `content/setups/battle.json`). In the
text view each unit is its archetype's glyph from the content, player 1
uppercase and player 2 lowercase: `A` assault, `E` engineer, `K` anti-armor,
`S` sniper, `B` dirt bike, `T` technical, `L` / `M` / `H` light, medium and
heavy rocket launchers, `U` ammo truck, `D` supply depot, `F` FPV drone, `^`
trap (shown to the observer even when hidden), `*` rocket in flight, `#`
rock. Drivers are inside their vehicles and not drawn.

The library targets `net8.0` (what Godot 4 uses) and `net10.0`; SimRun and
the tests target `net10.0`.

## Layout

| Path | What it is |
| --- | --- |
| `src/SimCore/FixedPoint` | Fixed-point math (1 cell = 1024 units), vectors, 32 facings |
| `src/SimCore/Rng` | Seeded SplitMix64; the only randomness allowed |
| `src/SimCore/Model` | The compiled content model (archetypes, components, warheads, factions), match setups and per-player unit definitions |
| `src/SimCore/Content` | Loads and checks content JSON files into a `ContentSet` (IO; runs before a match) |
| `src/SimCore/Sim` | World, units, commands, pathfinding (A*), targeting, hashing |
| `src/SimCore/Sim/Systems` | One file per system, run every tick in a fixed order (below) |
| `src/SimCore/Scenarios` | Starting maps; `battle` is the 96×96 Phase 1 two-player map, `sandbox` the same map with spawning on |
| `src/SimCore/Bots` | Scripted random player for recording test matches |
| `src/SimCore/Replays` | Record, save, load and play matches; read lobby files |
| `src/SimRun` | Headless runner with a text map view |
| `content/mvp` | The MVP Resistance roster as data |
| `content/setups` | Lobby files: each player's faction, presets and starting army |
| `tests/SimCore.Tests` | xUnit tests; `testdata/` holds the golden replay |

## Content model

Every unit is assembled from data, as designed in
[docs/design.md](docs/design.md#content-model). Content is every `*.json`
file under a directory (`content/mvp`), in any split; unknown fields are
errors, comments are allowed. Distances are in tenths of a cell, speeds in
tenths of a cell per second, times in ticks (20 per second), percents 0–100.

- **Armor classes and warheads** (`warheads.json`): the design's damage
  table. `null` means the warhead cannot target that armor class.
- **Archetypes**: a base body: tags, locomotor (`flying` puts it on the air
  layer), armor class, HP, speed, vision, trap detection, lifetime, fixed
  `components`, and for infantry the `slots` (`primary`, `secondary`) listing
  what a preset may pick for each. Vehicles have a `crew` driver seat.
- **Components**: parts with typed effects. Each effect kind is run by one
  system: `weapon` (combat and projectiles), `placer` and `launch`
  (abilities), `strike` (drones), `trigger` (traps), `supply`, `repair`,
  `modifier` (perks, applied when the match starts). `aura`, `builder` and
  `buildAssist` load but have no system until Phase 2. `placeholder: true`
  marks a stand-in for an undecided design choice (the pistol).
- **Factions**: allowed archetypes and pickable components, stat modifiers by
  tag, presets allowed per class (3 for the Resistance), and the tags a
  player is defeated without (`soldier` in Phase 1).
- **Match setup** (`content/setups/battle.json`): per player a faction,
  named presets (an archetype and one component per slot, in slot order) and
  a starting army (presets or slotless archetypes, with an optional driver
  per vehicle). It is checked against the content when the match starts,
  pinned to the content hash and seed, stored in the replay and hashed into
  every world hash. Machines with different content refuse to start.

The content hash is a SHA-256 over the compiled, ID-sorted definitions, so
file layout, order, whitespace and line endings don't change it.

Systems run every tick in this order: commands, vision (trap detection and
spotting), orders, boarding vehicles, abilities (traps and drones), ground
movement, air movement, combat, projectiles, drone strikes, trap triggers,
supply, repair, lifetimes, driver ejection, cleanup, match result.

Rules worth knowing:

- **Crew.** Vehicles spawn empty and can't move or fire without a driver.
  Soldiers walk to their own empty vehicle to get in; any driver is equal.
  When a vehicle is destroyed, the driver survives by a roll on the world's
  generator (`crew.survival`) and pops out damaged.
- **Weapons.** A unit fires its weapon with the most damage per tick against
  the target's armor class, among those in range that can hit the target's
  layer and armor and have ammo. Weapons only fire at enemies some friendly
  unit sees (vision), so long-range launchers need a spotter.
- **Weapon mode.** Each unit has a weapon mode, hashed with its state:
  `auto` (any weapon, the best for the target) or `primaryOnly` (only its
  first weapon). It starts as its archetype's `weaponMode` (anti-armor:
  `primaryOnly`, so he keeps to his launcher and never switches to a
  sidearm; everything else `auto`), and the owner changes it per unit
  with a `SetWeaponMode` command (`mode` field).
- **Attack ground** (`AttackGround` command, force-fire). Units walk into
  range of the cell and fire at its centre until given another order:
  each uses its first weapon (primary first) that can hit ground targets
  and has ammo, so an anti-armor soldier fires its AT missile and an AA
  soldier its rifle; a unit with only air weapons ignores the order.
  Bullets land on the point; splash (rockets, launcher salvos) hits
  whoever stands there, friends included. Ammo and reloads apply; the
  order ends when no ground weapon has ammo left. No vision is needed.
- **Projectiles collide.** Rockets and missiles fly along their aim line;
  each tick the segment they cover is swept against units (a circle of the
  archetype's `radius`, default by locomotor; structures by footprint) and
  blocked terrain. The first thing reached goes off there: full damage to
  the unit struck plus splash around the point. Friends count, the shooter
  and anyone inside a vehicle don't, and flying units only for weapons that
  hit the air. A projectile that reaches its aim point untouched goes off on
  the ground there. Launcher rockets fly an arc and only collide in the last
  2 cells. `World.Detonations` lists the tick's detonations (point, unit
  struck or 0, whether it hit a rock) for renderers.
- **Rocket truck** (`heavy_rocket_launcher`, a 30-tube launcher on a
  truck). It must deploy (`deployTicks`, 2.5 s) before it can fire and
  stow (`stowTicks`, 2 s) before it can drive; it deploys on its own when
  an attack or attack-ground order puts it in a firing position, and on a
  `Deploy` command; `Stow` packs it up. It fires only on orders (no auto
  targeting), only between its `minRange` (15 cells) and range (60 cells),
  and needs a spotter. A salvo ripples 30 rockets, one every 0.5 s,
  scattered about 3.5 cells; `Stop` ends a salvo at once and leaves it
  deployed. Its ammo counts rockets (`ammoPerRocket`), and only a supply
  depot reloads it (`depotReload`): never during a salvo, starting 1 s
  after the last shot, one rocket every 2 s within the depot's radius;
  ammo trucks don't. Splash damage falls from `splashCenter` to
  `splashEdge` percent (28% to 10% for its rockets; 100% to 50% by
  default).
- **Launchers** fire salvos of rockets that fly to where the target stood
  when the salvo began, with scatter and splash; they carry a few salvos and
  go quiet when empty.
- **Supply.** Ammo trucks and depots refill salvos and drone and trap
  charges in a radius; trucks have limited stock and restock at a depot.
  Units in a friendly supply radius reload 20% faster.
- **Drones** fly straight to a unit (following it) or a cell and explode on
  contact; AA missiles down them in one hit, bullets do 15%.
- **Traps** are hidden from the enemy until an enemy sniper or engineer
  comes near (revealed per player, for good); then anyone can shoot them.
  They fire once on an enemy ground unit, never on drones. A soldier keeps
  at most 3 planted; the next removes the oldest. `World.IsVisibleTo` is the
  renderer's "can player X see this" query.
- **Sandbox.** In a match whose scenario is `sandbox` (the battle map and
  armies), a `SandboxSpawn` command adds one of the issuing player's presets
  or slotless archetypes at a cell, through the command queue like any other
  order, so sandbox sessions replay exactly. Every other scenario ignores it.
  The Godot client uses it to put units under review on the map.
- **Match result.** A player with no soldiers left, on foot or driving, is
  defeated; `World.Result` reports when the match is over and who won.

## Determinism rules

`FixedPoint`, `Rng`, `Model`, `Sim`, `Scenarios` and `Bots` must follow
these. `DeterminismRulesTests` enforces the first five, and fails for source
in any other directory unless it is exempted on purpose (`Content` and
`Replays` do IO and never run inside a tick).

1. **No floating point.** No `float`, `double`, `decimal` or float math; use
   `Fixed`. Float results can differ between ARM and x86.
2. **No hash collections.** No `Dictionary`, `HashSet`, `GetHashCode` or
   LINQ operators built on them (`Distinct`, `GroupBy`, …): hash codes are
   randomized per process. Use lists sorted by ID.
3. **No time, environment, or global randomness.** No `DateTime`,
   `Stopwatch`, `Environment` or `System.Random`. Use the world's `Rand`.
4. **No threads.** No `Thread`, `Task`, `Parallel` or `async`. The tick runs
   on one thread.
5. **No `unsafe` or reflection.**
6. **Fixed order everywhere.** Systems run in a fixed order and visit units by ID.
   Every comparison used for sorting or picking a "best" breaks ties by ID.
7. **Everything that matters is hashed.** New state goes into `World.Hash`.

Integer arithmetic wraps on overflow (the build never enables overflow
checking), integer `/` and `%` truncate toward zero, and `>>` on signed values
is arithmetic: the same on every .NET platform.

## Tests that guard determinism

- `ReplayMatchesLiveMatch`: a recorded 10-minute bot battle, saved and
  loaded, replays to the same hash on all 12,000 ticks, twice.
- `GoldenReplay`: a committed replay (`testdata/battle_seed42.json`, replay
  format version 2, which carries the match setup) must produce committed
  hashes. CI runs it on Linux, macOS and Windows, so a cross-platform desync
  fails the build. After an intentional gameplay or content change,
  re-record: `SIMCORE_UPDATE_GOLDEN=1 dotnet test --filter GoldenReplay`.
- `BotMatchMatchesGoldenCommands`: recording the golden match again from
  scratch yields the committed command list, so the bots and RNG are exact.
- `EncodeMatchesGoldenBytes`: saving a replay writes the committed bytes.
- `RefusesOtherContent`: a replay played with different content refuses to start.
- `CommandOrderIndependentOfArrival`: peers receiving commands in a
  different order still agree.
- `RngTests.KnownSequence`: the generator's output is pinned.

## Not built yet

Phase 1 milestone 1 (the unit roster as data, and its behaviors) is done.
Not yet: unit collision and group steering (milestone 3), the Godot shell and
sprites (milestone 2), a tunable test bot (milestone 5; today's bot is
random), structures that block pathing, construction, training, workers
building or mining, gold and oil, fog of war (vision only gates firing),
tunnels, and networking. `aura`, `builder` and `buildAssist` effects load but
nothing runs them yet.

## Design

The game this engine is built for is designed in [docs/design.md](docs/design.md). [docs/resistance-mvp.html](docs/resistance-mvp.html) is the illustrated MVP walkthrough; download it and open it in a browser.

## License

MIT. See [LICENSE](LICENSE). This repository is the engine only; game art and content live elsewhere.
