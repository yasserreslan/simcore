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
heavy rocket launchers, `Q` heavy rocket truck, `W` twin missile truck, `C` cargo truck, `D` supply depot, `F` FPV drone, `^`
trap (shown to the observer even when hidden), `*` rocket in flight, `#`
rock. Drivers are inside their vehicles and not drawn.

The library targets `net8.0` (what Godot 4 uses) and `net10.0`; SimRun and
the tests target `net10.0`.

### Benchmarks

```bash
dotnet run -c Release --project src/SimRun -- -bench 500 -out bench/run_500.json
python3 bench/compare.py --before bench/baseline_*.json --after bench/index_*.json
```

`-bench N[,N...]` runs six workloads (`stationary`, `movement`, `combat`,
`projectiles`, `churn`, `obstacles`; pick with `-workloads`) at each unit count on
worlds it builds itself, for at most `-maxticks` ticks (400) or `-budget` seconds
(8) each, and writes JSON: tick time (p50/p95/p99/max), time per system (through
`World.Probe`, an `IStepProbe` called after every system), `World.Hash()` time,
bytes allocated per tick and path searches per tick (`World.PathRequests`).
Neither the probe nor the counter is game state; neither is hashed.
`bench/baseline_*.json` is main before the spatial index, `bench/index_*.json`
after it.

`-move-bench all|idle10k,moving10k,group500,group2000,pass,forest` measures local movement
at game scale: up to 10,000 units on a 1,500 x 1,500 map (`-map flat`, or a map made by the
game repo's `tools/terrain/make_heightmap.py --size 1500 --bench-layout --out DIR/map`, which
also writes `map_layout.json`: a mountain ridge cut by three passes, massifs, graded winding
roads, forests). A bench driver stands in for routing (one flow field per group toward
formation slots); it reports tick and movement time, max overlap, stuck counts and arrival.
Results: `bench/move_1500.json`, `bench/move_flat1500.json`.

### Movement

`Sim/Systems/MovementSystem.cs` moves every ground unit standing on the map along its
`MoveIntent` (written by the path system): terrain speed per movement class (slope up or down,
forest), avoidance (dodging, keep right when oncoming, separation in same-direction streams),
collision (unit circles; blocked cells, terrain the class can't enter and structure boxes are
hard, the unit's radius being its clearance), vehicle turning and acceleration (no turning in
place; backing up when boxed in), arrival and stuck detection. Integer only; neighbours'
contributions are sums or visited in ID order, so the result never depends on visiting order
(`MovementTests` checks it). Its scratch (`Sim/Movement/MoveScratch.cs`) is not game state.

### Spatial index

Vision (detection and spotting), target acquisition, splash and projectile hits
look units up in a uniform grid of 8-cell buckets (`Sim/SpatialIndex.cs`,
`Sim/Spatial.cs`) instead of scanning every unit. It is scratch space, rebuilt
from the unit list (in ID order) before vision and before combat, never hashed.
It only narrows the candidates: each system still applies its exact test, in an
order that gives the same result as the old full scan (ID order where order
matters, or an explicit ID tie-break). `SpatialIndexTests` replays random dense
worlds and the golden battle with and without it (`World.FullScan`) and checks
the hashes agree on every tick.

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
| `src/SimRun` | Headless runner with a text map view, and the benchmark (`Bench.cs`) |
| `bench` | Benchmark results (JSON) and `compare.py` |
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
supply, repair, lifetimes, driver ejection, building collapse, cleanup,
garrisons (stages, capacity, ownership), match result.

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
  2 cells; one coming down over a structure's footprint hits its roof. `World.Detonations` lists the tick's detonations (point, unit
  struck or 0, whether it hit a rock) for renderers.
- **Aiming travelling shots.** Rockets and missiles lead a moving ground
  target: they aim where it will be after the flight time (its speed toward
  its next path cell), so a target driving straight is hit and one that
  turns or stops can still dodge. A weapon's `autoRange` (per armor class)
  limits what it engages on its own: the AT missile auto-engages infantry
  only within 4 cells (and not one running away, `autoSkipRetreating`),
  and such targets come after vehicles and structures; after `maxMisses`
  (3) misses in a row on the same unit it leaves it. Attack orders are
  always obeyed.
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
  after the last shot, one rocket every 2 s within the depot's radius,
  for 10 supply each out of the depot's stock. Splash damage falls from `splashCenter` to
  `splashEdge` percent (12% to 4% over 3 cells for its rockets, so a full
  salvo on a clustered group kills about half the infantry and badly damages
  the vehicles; 100% to 50% by default).
- **Construction** (`construction` on a structure archetype: the supply depot, 6x6 cells as its
  art). A worker (`worker_build`: a `builder` at rate 100, range 1.5 cells) given `Build`
  (archetype, top-left cell, `facing`: a quarter turn only for a square footprint) places a site
  at once, provided:
  - every footprint cell is on the map, passable and free of structures;
  - no ground unit stands in it but the builders;
  - on terrain, every cell is Flat or Gentle for the builders.
  On terrain the site's ground becomes a pad at the footprint's mean height, easing back over
  `padBlend` (3) cells.

  The site starts at `startHp` (10%) of its HP, rising with the work to full (damage taken
  stays), and does nothing until finished: no supply, no vision, no cargo unloading. Workers
  within range add work each tick (`Unit.Work`, rate percent times ticks, done at
  `buildTime` x 100). The first worker adds its full rate, a second half (`helpers`
  [100, 50, 0]), a third nothing. With nobody working it waits, and `Resume` sends any worker
  back to it. Stages 0-4 begin at `stages` (15/40/70/95%) of the work
  (`World.ConstructionStage`). `CancelBuild` removes the site, and `refund` (75%) of its cost
  will come back once there is an economy (charge and refund are no-ops until then). A depot a
  worker builds starts with `constructedStock` (0); one a scenario places starts full.
  `World.CanPlace` is the placement check, shared with the client. Sites and builders hash only
  when present.
- **Garrisonable buildings** (`garrison` on a structure archetype:
  `block_panel_5`, 23x6 cells, holds 10; `block_brick_4`, 14x7, holds 6; the town
  buildings `house_small` 5x5, holds 4, 700 HP; `villa_walled` 8x9, holds 6, 1000 HP;
  `shop_row` 7x6, holds 6, 1100 HP; `skeleton_frame` 8x6, holds 8, 1300 HP).
  They spawn neutral (owner 0) and block their footprint for ground units;
  the rubble keeps blocking after they fall. Infantry (workers too) given an
  `Enter` order on one walk to its nearest door (`doors`, cells just outside
  the footprint) and go in while there is room; a building belongs to
  whoever is inside, turns neutral when empty, and nobody enters one held by
  the enemy. `Exit` on a soldier inside lets him out at the door nearest the
  command's cell; on the building it evacuates everyone (spread over the
  doors when the cell is off the map). Occupants can't move, aren't visible
  or targetable (attacks target the building), and fire from inside with
  their own weapons, range and vision counting from the building's edge
  nearest the target plus `rangeBonus` (2 cells) and `visionBonus` (4).
  Rockets they fire fly out through their own walls. Bullets and sniper
  rounds only scratch the building (building armor); explosive warheads
  (`occupantWarheads`: rocket, explosive, so the RPG and the rocket truck)
  hit the building and every occupant takes `occupantDamage` (25%) of what
  the hit would do to him. Damage stages follow HP (`stages`: above 80%,
  60%, 35%, then above 0); each stage holds `stageCapacity` percent
  (100/100/75/50) and the excess is thrown out (highest ID first, losing
  `ejectDamage` 20% of max HP). At collapse each occupant survives with
  `collapseSurvival` (50%, the world's generator, occupant ID order) at
  `collapseHp` (30%) of max HP, and is put out at the doors. Neutral
  buildings are never auto-targeted. Splash counts from a structure's
  nearest footprint point, and so do weapon range and vision to it (and a
  structure's own vision, from its footprint edge).
  Buildings don't block line of sight yet (no LOS in the sim).
- **Heavy rocket truck** (`heavy_rocket_truck`): slower (speed 16) and tougher
  (300 HP) than the rocket truck; deploys in 4 s, stows in 3 s, fires only on
  orders and needs a spotter. It fires 12 big rockets, one every 2 s, from 50 to
  90 cells (nothing closer than 50); they fly a high arc at 14 cells/s with 4
  cells of scatter and collide only coming down (last 2 cells, or the roof of a
  building). Warhead `heavy_rocket`: 400 damage, 100% to infantry and light
  vehicles, 200% to buildings, so 3 hits bring down a panel block (2400 HP), 2 a
  brick block (1600 HP), and a direct hit destroys a technical (180 HP); the
  5-cell splash falls from 100% at the centre to 10% at the edge (lethal to
  infantry near the centre). Occupants of a hit building take 25% of it. Only a
  supply depot reloads it: nothing during a salvo, 1 s after the last shot, then
  one rocket every 5 s (60 s for all 12).
- **Twin missile truck** (`missile_truck`): 2 heavy missiles side by side,
  fired 6 s apart, from 45 to 100 cells on a high arc with 3 cells of
  scatter; deploys in 6 s, stows in 5 s, fires only on orders, needs a
  spotter. Warhead `heavy_missile`: 550 damage, 220% to buildings (2 hits
  bring down a panel or brick block). Its splash has two rings (the
  weapon's `splashInner` and `splashOuterPercent`): full damage within 3
  cells, killing every unit, and a flat 30% (165) out to 9 cells, which
  infantry don't survive. It reloads only at a depot and only stowed
  (`depotReload.stowed`: a deployed truck at a depot with nothing to fire
  at packs up first): one missile every 10 s, 1 s after the last shot,
  for 150 supply each.
- **Tactical missile truck** (`ballistic_truck`): `ballistic_missile`, one
  ballistic missile from 60 to 160 cells. Its 10 s deploy is the aim: it stands
  still while the erector rises, and moving cancels it. The missile flies 1.8x the
  twin missile's speed (25.2 cells/s) on a steep arc rising 45% of the distance
  (the weapon's `arc`). It does 730 damage (`heavy_missile`: 1606 to buildings, so
  a brick block falls in 1 hit and a panel block in 2) in a two-ring splash 10%
  bigger than the twin missile's: full within 3.3 cells, 30% (219) out to 9.9.
  It reloads only at a depot, stowed: 60 s, for 250 supply. The weapon's
  `interceptible: "ballistic"` marks its projectiles (`Projectile.Interceptible`,
  hashed when set) for future interceptors; none exist yet. It is the slowest
  launcher: 1.1 cells/s, 70% of the heavy rocket truck, and 2 s to reach that
  (`accelTicks`, default 5).
- **Suicide drones** (one-way attack drones, the weapon's `drone`): fired at a ground point
  fixed at launch (attack ground). Each one leaves its rail at `launchHeight` and climbs at
  `climbRate` (12 m/s) to `cruiseAboveMax` (30 m) over the map's highest ground (on a flat map,
  30 m). It cruises there at 12 cells/s, then dives at `dive` (1000 permille = 45 degrees) onto
  the point; range up to 600 cells. Its path is sampled 8 times a cell:
  - ground above it sets it off in any phase (a hill in the climb);
  - in the dive, the first structure (footprint, up to its archetype `height`), vehicle or
    soldier (its radius, up to its height) sets it off, else the ground at its point;
  - it cruises above everything.
  The projectile's flight (`Phase`, `Altitude`, the dive's start, `Age`) is hashed, and it is
  marked `interceptible: "drone"`. The charge (`drone_charge`) does 260 damage in a 4-cell blast
  (100% to 40%): infantry die, vehicles up to 260 HP die, and 300-HP trucks survive hurt. On a
  building every hit takes exactly 20% of its max HP (the warhead's `maxHpPercent`), so 5 drones
  bring any building down.
  - **Drone launcher truck** (`drone_truck`, `drone_rails`): 2 drones, one per designated point.
    Attack ground sets the first; a second attack ground while the first is pending (or one with
    `queue`, Shift+G) sets the second (`Unit.NextGround`). It deploys in 2 s, fires the first
    drone, then the second 3 s later if there is one, else it stops at one. It reloads only at a
    depot, stowed: 30 s and 150 supply a drone. 200 HP, speed 2.5, cost 1200.
  - **Drone station** (`drone_station`, placed with the engineer's `drone_station_kit`): the
    engineer carries one kit and stands 2 s at the spot (the placer's `placeTicks`). The station
    holds one drone (`noResupply`) and its owner fires it remotely with attack ground, whether or
    not the engineer still lives. Spent, it stays 20 s (`expireWhenEmpty`). It has 60 HP. Only a
    depot refills the kit (the placer's `depotReload`: 10 s, 150 supply).
- **Rocket jeep** (`rocket_jeep`): `jeep_rockets`, 2 heavy rockets 3 s apart
  from 20 to 50 cells (450, `heavy_rocket`: 900 to buildings), a 4-cell
  splash from 100% to 20%, 3 cells of scatter; deploys in 3 s, stows in 2 s;
  the technical's speed; reloaded only stowed at a depot, one every 6 s for 80.
- **Kornet team** (`kornet`): a guided missile (`kornet_atgm`, warhead
  `atgm`). The weapon's `aimTicks` (20) is aimed, standing still, before
  every shot; its `guided` block makes the projectile steer toward its
  target each tick (velocity change capped at `turnRate`, compiled to
  thousandths of the speed per tick, all integer) for up to `maxFlight` (36
  cells) while the target lives, stays within maxFlight of the operator and
  the operator (`requiresOperatorStill`) stands still on the attack; then it
  flies straight on `dropDistance` (3 cells) into the ground. It hits only
  its target (or a rock). Fired at a point (attack ground) it aims the same,
  is guided onto the point and stops at the first building wall on the way
  (so attack ground on a building hits it). The shooter reloads (5 s) from the moment it stops
  guiding. `priority` orders automatic targets by armor class (vehicles,
  buildings, infantry). Armor class `medium_vehicle` is the launchers';
  every older warhead treats it like `light_vehicle`.
- **Launchers** fire salvos of rockets that fly to where the target stood
  when the salvo began, with scatter and splash; they carry a few salvos and
  go quiet when empty.
- **Supply.** Only supply depots rearm: they refill salvos and drone and
  trap charges in their radius, and units in a friendly depot's radius
  reload 20% faster. A depot holds a finite stock (`supply.stock`, 1000,
  starting full) that never refills by itself: each salvo or charge it
  gives costs 1, and launchers it reloads round by round pay their
  weapon's `depotReload.cost` per rocket (rocket truck 10, heavy rocket
  truck 40). When the stock is below a round's cost the launcher waits at
  the depot (`Unit.DepotStarved`) with its reload count reset.
- **Cargo trucks** (`cargo_truck`, `cargo` effect) refill depots; trucks
  no longer rearm units themselves. A cargo truck is full (400 supply,
  as it spawns) or empty. An `Unload` command onto its side's depot
  drives it into the depot's radius; a full truck standing still in a
  friendly depot's radius (sent there or not) unloads: 60 ticks (3 s)
  standing there, then the whole load goes into the stock at once. It
  starts only when the depot has room for all of it (counting trucks
  already unloading there) and otherwise waits beside it, so nothing is
  wasted. Moving off, leaving the radius or losing the depot cancels the
  unload and the truck stays full; an empty truck does nothing (supply
  routes come later).
- **Drones** fly straight to a unit (following it) or a cell and explode on
  contact; AA missiles down them in one hit, bullets do 15%.
- **Traps** are hidden from the enemy until an enemy sniper or engineer
  comes near (revealed per player, for good); then anyone can shoot them.
  They fire once on an enemy ground unit, never on drones. A soldier keeps
  at most 3 planted; the next removes the oldest. `World.IsVisibleTo` is the
  renderer's "can player X see this" query.
- **Sandbox.** In a match whose scenario is `sandbox` (the battle map and
  armies), a `SandboxSpawn` command adds one of the issuing player's presets
  or slotless archetypes at a cell (a building at exactly that top-left
  cell, neutral, when its footprint is free), through the command queue like any other
  order, so sandbox sessions replay exactly. Every other scenario ignores it.
  The Godot client uses it to put units under review on the map.
- **Match result.** A player with no soldiers left, on foot or driving, is
  defeated; `World.Result` reports when the match is over and who won.
- **Terrain (data).** A map may carry a terrain (`World.AttachTerrain`,
  `World.Ground`, `Sim/TerrainMap.cs`): corner heights (`HeightAt`), per-cell
  grade, forest density (0-3), roads and building pads, and one path-cost byte
  per cell per movement class (`content/terrain/rules.json`: foot, wheeled,
  heavy wheeled, tracked; slope classes Flat / Gentle / Steep / Cliff, road and
  forest costs). Maps load from JSON plus a 16-bit PNG heightmap
  (`Content/TerrainLoader.cs`). A map without terrain is flat and hashes as
  before; the rules are not part of `content/mvp` for that reason. Load time
  and memory: `SimRun -terrain-bench 1500` (about 80 ms and 18 MB). See
  `docs/design.md`, "Terrain".

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
- `SpatialIndexTests`: the spatial index gives the same world as full scans, tick
  for tick, on random worlds and the golden battle; queries find every
  overlapping unit in ID order.

## Not built yet

Phase 1 milestone 1 (the unit roster as data, and its behaviors) is done, and
a private Godot client (milestone 2) already draws the sim in real-time 3D.
Not yet: unit collision and group steering (milestone 3), a tunable test bot
(milestone 5; today's bot is random), line of sight (buildings don't block it), construction, training, workers
building or mining, gold and oil, fog of war (vision only gates firing),
tunnels, and networking. `aura`, `builder` and `buildAssist` effects load but
nothing runs them yet.

## Design

The game this engine is built for is designed in [docs/design.md](docs/design.md). [docs/resistance-mvp.html](docs/resistance-mvp.html) is the illustrated MVP walkthrough (it predates the move from sprites to real-time 3D); download it and open it in a browser.

## License

MIT. See [LICENSE](LICENSE). This repository is the engine only; game art and content live elsewhere.
