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
`S` sniper, `M` the Sentinel's assault medic (the Resistance's medium launcher elsewhere), `B` dirt bike, `T` technical, `Y` the Sentinel's Humvee, `I` the Sentinel's Abrams tank, `l` the Sentinel's Booker light tank, `L` / `M` / `H` light, medium and
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

`-bench-air N` runs the air bench: N Sentinel drones in mission modes (Guard, Patrol, Search,
Roam, orbit) over 300 ground units on a 256-cell map, against the ground alone (E3's gate), then
N test fixed wings (`content/fixtures/fixed_wing_test.json`) in the same modes (E4), N real jets in
air-to-air combat (`dogfight`, E6) and N real jets striking the ground (`strike`, E7: bombs, strafing).

`-bench-heli N` runs the helicopter bench: N Sentinel helicopters (half attack, a quarter armoured, a
quarter light) on attack-move into a ground army of 300 with its AA soldiers and air defence vehicles, on a
256-cell map with ridges, against the ground alone. 100 helicopters add about 1.6 ms of tick p99 and 0.5 ms
mean (M3 Pro, 2026-10-21).

`-move-bench all|idle10k,moving10k,group500,group2000,pass,forest` measures local movement
at game scale: up to 10,000 units on a 1,500 x 1,500 map (`-map flat`, or a map made by the
game repo's `tools/terrain/make_heightmap.py --size 1500 --bench-layout --out DIR/map`, which
also writes `map_layout.json`: a mountain ridge cut by three passes, massifs, graded winding
roads, forests). A bench driver stands in for routing (one flow field per group toward
formation slots); it reports tick and movement time, max overlap, stuck counts and arrival.
Results: `bench/move_1500.json`, `bench/move_flat1500.json`.

`-spike-bench move,land,field,sectors [-map DIR/map.json] [-prepare true] [-fog true]` times single
ticks, each workload four times in one process (the first cold): `move`, 38 crewed technicals
given one Move order after 20 s on a 256-cell map; `land`, a gun-killed jet's pilot landing among
38 enemy technicals; `field`, the tick after an airfield is laid finished (`World.SpawnAirfield`,
its ground levelled); `sectors`, what building each routing class's HPA* sectors costs. It prints
the worst ticks with their slowest systems.

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
| `src/SimCore/Bots` | Scripted random player for recording test matches (`Bot`), and a fog-honest skirmish AI (`SkirmishAi`) |
| `src/SimCore/Replays` | Record, save, load and play matches; read lobby files |
| `src/SimRun` | Headless runner with a text map view, and the benchmark (`Bench.cs`) |
| `bench` | Benchmark results (JSON) and `compare.py` |
| `content/mvp` | The MVP Resistance roster as data |
| `content/setups` | Lobby files: each player's faction, presets and starting army |
| `content/fixtures` | Test content loaded beside `content/mvp` by tests and benchmarks only (the test fixed wing) |
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
  `modifier` (perks, applied when the match starts), `aura` (healing: the
  aura system; an aura's `modifiers` load but nothing applies them yet), `smoke` (smoke
  launchers: the smoke system).
  `builder` and `buildAssist` load but have no system until Phase 2. `placeholder: true`
  marks a stand-in for an undecided design choice (the pistol).
- **Aircraft and air combat** (design: [docs/design.md](docs/design.md), "Air combat", whose
  "Content fields" table lists every field). `air_classes.json` lists what air defence and
  air-to-air weapons engage a target as (`airClasses`: `id`, `kind` `projectile` or `aircraft`; a
  class's value is its position, so new classes go at the end). A flying archetype has a `flight`
  block (`kind` `drone` or `fixedWing` with a `fixedWing` envelope; its class in `interceptible`;
  `flyZoneMargin`, how far past the map's edge it may fly) and may carry `mission` (Guard's leash and default filter), `signature` (`detect` /
  `lock` / `kill` percents), `countermeasures` (flares, chaff: salvos per sortie, refilled on a
  Rearm), `evade` (a fixed wing's break) and `loss` (the wreck's `crash` path and impact, the
  pilot's `eject`); an Air-layer archetype without a flight may be an air target with `airBody`
  (the FPV drone). Weapons carry `intercept` (missiles: classes, per-class `killBest`, `speedRefs`,
  `hitDamage`, `hits`, `seeker`, `boresight`, lock and fuse), `airGun` (a gun's cone and per-class
  burst chances, `strafeRange` / `strafeEvery` for strafing, a `ceiling` for a ground gun such as
  the technical's heavy MG) or `delivery` (a bomb's profile, release height, angles, IP distance,
  scatter, guidance, stick and `gravity`; H2: fire `modes` (`id`, `salvo`: bombs a pass at each aim point,
  the first the default), `group` and `groupAside` (queued aims bombed in one pass) and `retard` (a high-drag
  bomb); H4: `overfly` (fly on over the last drop before breaking) and a mode's `spread` (a carpet: one stick for
  the whole pass); the supersonic bomber: a mode's `across` and `acrossSpacing` (rows of bombs across the path)) or `sideFire` (H3: a gunship's side cannon: `scatter`, look `tier`, `priority` armour classes, `areaFire`,
  `pick`); an interceptor may have a height `ceiling` (H3: the AA soldier's). A gunship archetype's `gunship` block
  (`radius`, `area`, `onCircle`, `bearing`) is its orbit attack. An airfield facility's `production` with `intoStation`
  builds aircraft into a free station of its airfield (the jets). Helicopters: a `flight` of `kind` `rotary` with a
  `rotary` block (`maxSpeed`, `sideSpeed`, `accel`, `yawRate`, `descentRate`, `attackHeight`, `hideHeight`, `popMax`,
  `standoff`, `coverSearch`, `gunRange`, `leash`, `spool`, `outOfControl` (`ticks`, `hpPercent`, `speedPercent`,
  `spinRate`, `endHeight`), `crew` (`count`, `survive`, `unit`), `riders` (`seats`, `tags`, `boardRange`,
  `dropHeight`, `dropTicks`, `survive`)) and an `aircraft` block with `landing` `vertical`; a weapon's `cone` (its mount:
  degrees off the nose), `guided.fireAndForget` (its own seeker), `intercept.outright` (a hit destroys a helicopter
  outright); an archetype's `groundRadarVehicles` (a ground radar that spots vehicles only: the mast radar), `cueLock`
  and `sweepTicks` (the mobile radar: its side's ground air defence locks faster on what it tracks; its antenna's turn);
  an interceptor's `battery` (`linkRadius`, `radarTags`, `semiActive`: the SA-6 launcher fires only on a linked radar's
  track, and its missile needs it to the fuse); a structure's
  `helipad` (`refuel`, `rearm`, `repair` percents) with `production.intoStation` builds onto itself.
- **Factions**: allowed archetypes and pickable components, stat modifiers by
  tag, presets allowed per class (3 for the Resistance), and the tags a
  player is defeated without (`soldier` in Phase 1).
- **Airfields** (`airfields.json`, `archetypes/airfield.json`; design: the game repo's
  `docs/airfield-design.md`): an `airfieldTypes` entry is an airfield type: the largest
  aircraft class it serves (`maxClass`; it serves every smaller one), its pieces (facility,
  row, runway segment, loading and normal station archetypes), its grid (station, lane,
  runway segment, grass, taxiway, connector, minimum segments, approach corridor, facility
  gap, the ground relief allowed), the row's station slots (kind, park-in and park-out
  paths, heading, pushback), holding, lock and taxi-wait times, collapse damage, the damage
  of running into a crater (`craterHitDamage`) and the crater and wear numbers. Each piece archetype has an `airfield` block naming its `role`
  (stations add `kind`, `needsFacility`, `rearm` / `repair` / `refuel` percents and
  `maxSpan`). Structures may set `pavement` (no route or movement block; road on terrain),
  `targetable: false`, `invulnerable`, `rubble.hpPercent` and
  `repairable.fullPercentOfBuild`. Aircraft carry an `aircraft` block: `sizeClass`
  (S, M, L, XL), `span`, `length`, `landing` (`runway` or `vertical`), `minLandRun`,
  `minTakeoffRun`, the `approach` (final fix distance and height, touchdown, flare,
  roll-out deceleration, braking), `speeds`, `service` (refuel, rearm per round, repair at
  1x), ground vision and eye, the armor class it has on the ground (`groundArmor`), hold
  reserve and `sortie`; since E9 also `approach.pattern` (`straightIn`, the default, or
  `overhead` with `initialDistance`, `patternHeight` and `breakSpeed`), `approach.aerobrakeSpeed`
  (the nose held up after touchdown), `speeds.militaryAccel` (a dry-power takeoff roll; then
  `takeoffAccel` is the afterburner's) and `corridorNeed` (the corridor beyond the approach end
  it needs on the map to use a field; a drone without one has its straight-in join checked
  instead, E11). Weapons and strikes may say what
  their blast does to a runway (`runway`: a `crater`, `small` or `large`, or `wear`).
  Everything is checked against the type (footprints, slots, roles) when content loads.
- **Match setup** (`content/setups/battle.json`, a Resistance mirror;
  `content/setups/sentinel.json`, Sentinel against Resistance): per player a faction,
  named presets (an archetype and one component per slot, in slot order) and
  a starting army (presets or slotless archetypes, with an optional driver
  per vehicle). It is checked against the content when the match starts,
  pinned to the content hash and seed, stored in the replay and hashed into
  every world hash. Machines with different content refuse to start.

The content hash is a SHA-256 over the compiled, ID-sorted definitions, so
file layout, order, whitespace and line endings don't change it.

Systems run every tick in this order: commands, vision (spotting, then trap
detection), orders (with the unit AI), boarding vehicles, abilities (traps and drones), ground
movement, air movement, combat (crushing first), air defence, projectiles, drone strikes, trap triggers,
supply, repair, auras, lifetimes, tunnels, airfields, driver ejection, building collapse, cleanup,
garrisons (stages, capacity, ownership), match result.

Rules worth knowing:

- **Crew.** Vehicles spawn empty and can't move or fire without a driver.
  Soldiers walk to their own empty vehicle to get in; any driver is equal.
  When a vehicle is destroyed, the driver survives by a roll on the world's
  generator (`crew.survival`) and pops out damaged.
- **Passengers.** A vehicle with `crew.passengers` seats (the infantry carrier:
  15) also takes riders with one of `crew.passengerTags` once its driver seat is
  taken. Riders can't be seen, hit or fire out (no fire ports). Exit on a
  rider lets just him out behind the vehicle; Exit on the vehicle lets every
  rider out (the driver stays; with no riders it lets the driver out). When it
  is destroyed every rider rolls the same `crew.survival` / `crew.exitDamage`
  as the driver, in ID order after the driver. `World.PassengersOf` lists them.
  With `crew.unloadInterval` (the Humvee: 8) Exit on the vehicle lets them out one
  by one instead: the first at once, then one every that many ticks
  (`Unit.DismountWait`, `Unit.Dismounted`); 0 (the infantry carrier) is all at once.
- **Weapons.** A unit fires its weapon with the most damage per tick against
  the target's armor class, among those in range that can hit the target's
  layer and armor and have ammo. Weapons only fire at enemies some friendly
  unit sees (vision), so long-range launchers need a spotter.
- **Fog of war** (`MatchSetup.Fog`, `"fog": true` in a setup; off by default, and the setup
  digest changes only when it is on). Each player has a `FogGrid` (`World.FogOf(p)`): per cell,
  how many of its viewers see it now, and whether it was ever seen (explored; display only, the
  sim never reads it), with a version and a dirty box for renderers. Every unit with vision that
  is built and not inside a vehicle is a viewer, swept again only when its cell, owner, radius
  or eye height changes. Ground viewers cast integer rays to the edge of their box with two
  horizons: the ground's (a crest hides what is behind it) and the treetops' (ground plus
  canopy: what is behind trees is hidden once the forest crossed reaches the budget). Air
  viewers see their whole disc. Ground units are spotted from the grid; air units by distance
  as before, or within an air defence unit's `airVision`. `World.IsVisibleTo` then means "spotted
  by your side" for enemies (`Unit.SeenBy`), so ordered attacks need a spotter again;
  `World.IsRevealed` is the fog-free rule that shells and blasts use. The rules (eye and target
  heights, canopy per density, forest sight and budget) are the optional `vision` block of the
  terrain rules (`content/terrain/rules.json`). Tests: `FogTests`.
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
- **Vehicle garage** (`vehicle_garage`, 20x11 cells as its art, 2500 HP, built by workers in 40 s): a
  factory (`production` on a structure). `Produce` queues a vehicle (any archetype with one of
  `production.tags`, here `vehicle`; up to `queueMax` 10 waiting and in production).
  - **Production:** its 2 `bays` produce at once, the oldest order first; each takes its
    archetype's `buildTime` (now set on every vehicle: technical 10 s, launchers 20-30 s, the
    tactical missile truck 40 s). A finished vehicle hands its bay to the next order as soon as
    it has a door.
  - **Doors:** 4, one per `production.exits` entry (the art's door_00..03). A finished vehicle
    takes a random free door (shut, nothing behind it) other than the door the previous vehicle
    took: `World.PickDoor`, a SplitMix64 roll seeded by the match seed, the tick, the garage's ID
    and its door serial (`DoorSerial`, one per vehicle), so it never touches the world's RNG
    stream. **Rule when only the previous vehicle's door is free:** the vehicle waits up to 2 s
    (40 ticks) for another; then it takes that door. A factory with one door just uses it.
  - **Driving out:** the door opens (`doorTicks`, 1.5 s). The vehicle then appears with its crew
    aboard (`production.crew`, a Rifleman) on its door's bay floor inside the garage
    (`World.BayPoint`: `production.bayPoints`, the art's bay_00..03, 7.5 m inside) and gets a
    real move order to the rally point, driven by the movement system at its own speed and
    acceleration. Its route starts at the door's exit (`World.ExitPoint`: `production.exits`,
    exit_00..03, 3 m outside the south side; `Unit.OutVia`), and while it drives out its own
    garage's footprint doesn't block it (`Unit.OutOf`: movement skips that garage's box for it,
    so its door lane is passable for it alone). `OutOf` clears once it no longer touches the
    footprint, or when its door comes down. The door closes once the vehicle is out and has left
    the exit lane (4 cells deep).
  - **The exit lane:** friendly units standing in it are told to step across it, the nearest free
    cell 5 cells to their side. Enemy units block it, and after `pushAfter` (10 s) the vehicle
    comes out anyway.
  - **Rally:** `SetRally` sets a cell, or next to a unit or building. The default is
    `production.rally`, the art's rally, 10 m in front.
  - **Cancel:** `CancelProduce` drops the last order, a bay's, or a waiting one (`Index`).
  - **Limits:** a site produces nothing, nor does a garage below `minHpPercent` (35%, damage
    stage 3, where its doors are wreckage), and a destroyed garage loses its queue. Cost is a no-op
    until there is an economy.
  - **Hashing:** the queue, bays (order, ticks left, wait for a door), doors (state and ticks, the
    unit behind it, the vehicle driving out, its wait), the last door, the door serial and the
    rally are hashed for factories only; `OutOf` and `OutVia` only for a vehicle driving out.
- **Military garage** (the Sentinel's `military_garage`, 40x18 cells, 4000 HP, built by a worker or the bulldozer in
  60 s, cost 4000; `service` block; `Sim/Systems/ServiceSystem.cs`; `MilitaryGarageTests`; design: the game repo's
  `docs/military-garage-design.md`). A factory like the vehicle garage (any `vehicle`, 10 orders) whose **5 doors are
  shared bays**: a bay takes a free door when it takes an order and builds behind it (`Bay.Door`), at most 3 at once
  (`bays`, counting finished vehicles still coming out), and every other door **services** one of its side's vehicles.
  - **Service** (`CommandKind.Service = 34`, Target the garage; `OrderKind.Service`): only its side's ground vehicles with
    a `service.tags` tag (not aircraft, drones, infantry or structures), with a driver. Each drives toward the nearest
    door; within 8 cells of a door's exit it waits (`ServiceStage.Waiting`); free doors go to waiting vehicles by the
    tick they got there, then ID, each the free door nearest to it. It drives to that door's exit point, the door opens
    (`doorTicks`), it drives straight in to the bay floor through the footprint (`OutOf`, as a produced vehicle drives
    out) and is in its bay. In parallel: every weapon refilled after `reloadTicks` (5 s), HP rising evenly to full over
    `repairTicks` (10 s), an empty cargo truck refilled after `cargoTicks` (10 s). Done, it drives out through the same
    door to the rally point and the door closes once it has left the exit lane. Free of charge (no economy yet).
  - **In the bay** a vehicle is off the map like a unit underground (`Unit.Inside` = `World.InGarage`, `InGarage`):
    nothing sees, targets, hits or pushes it, it neither sees nor fires, and orders don't reach it (the client draws it
    from the door). Another order or Stop while it drives in sends it back out (Stop: to the rally); before that the
    order just ends.
  - **Limits:** a garage that is unfinished, rubble, destroyed or below its production `minHpPercent` (35%) services
    nothing: vehicles inside or driving in come out to the rally point, the others' orders end, new orders are ignored.
    Production behaves as before; the vehicle garage (no `service`) is unchanged.
  - **Queries:** `World.DoorOccupantOf(garage, door)` (use, vehicle, what is built behind it, bay), `World.DoorOf(unit)`
    (garage, door, production or service), `World.ServiceProgressOf(garage, door)` (ticks left and total of the reload,
    repair and cargo refill; total 0: not needed), `World.ServiceQueue(garage)`, `World.ServiceWorks`; doors as before
    (`Unit.Doors`: state, `Use`, `Serviced`); `Unit.ServiceAt`, `ServiceStage`, `ServiceDoor`.
  - **Hashing:** the doors' use and service state and the bays' doors only for garages with `service`; a vehicle's service
    state only while it has one: games without them hash as before.
- **Assault medic** (`sentinel_medic`, the Sentinel's first soldier of its own; `medic_kit`; `Sim/Systems/AuraSystem.cs`;
  `MedicTests`; design: [docs/design.md](docs/design.md), "Assault medic"). A rifleman with an aid bag: 75 HP (the
  rifleman 60), speed 18 (19), vision 7 cells, the rifle, 260; a soldier (counts against defeat, may drive); no
  loadout. Two start in the Sentinel lobby; the sandbox spawns him by archetype. His `aura` heals 3 HP every 30 ticks
  (2 HP/s) to his side's units with the `infantry` tag within 6 cells, never above max HP, under fire too.
  - **Aura content:** `aura: { heal, everyTicks, radius, affects: { relation: ally|enemy, tags } }` on any component.
  - **No stacking:** a unit takes at most one aura heal per interval however many auras reach it: the strongest (HP
    per tick, then the larger heal, then the lowest source ID), then none for that aura's `everyTicks`
    (`Unit.HealReady`, hashed only once set: games without auras hash as before).
  - **Who:** a source heals only from the map (alive, built, not inside anything) and never itself (another medic
    heals him). Nobody inside a vehicle, a building, a tunnel or a garage bay is healed: a garrison is out of the
    medic's reach, and a building the enemy can hurt only with explosives must not also regenerate its occupants.
- **Humvee** (`humvee`, 2026-10-24; `humvee_mg`, `smoke_launchers`, the `heavy_bullet` warhead; `Sim/Systems/SmokeSystem.cs`;
  `HumveeTests`; design: [docs/design.md](docs/design.md), "Humvee"). An up-armoured 4x4 with a gunner turret; it
  replaces the technical in the Sentinel's roster and lobby (the Resistance keeps it): `medium_vehicle`, 400 HP, speed
  40 (the fastest Sentinel ground vehicle), radius 8, 500, 15 s in either garage. A driver plus 5 `infantry` passengers
  who get out 0.4 s apart (`crew.unloadInterval` 8: all 5 in 1.6 s).
  - **Gun:** `humvee_mg`, ground only, 14 at 7 cells every 5 ticks, on the move; `heavy_bullet` is the bullet's table
    with 125% against infantry (17 a burst; the technical's heavy MG 12).
  - **Smoke** (`smoke: { charges, radius, duration, cooldown }` on any component; the Humvee: 2, 6 cells, 10 s, 2 s):
    `CommandKind.Smoke = 41` pops a screen around each listed unit where it stands (a charge; needs a driver). A sight
    line crossing a screen is blocked unless both ends stand in it (an air viewer is never in one): enemies outside lose
    what stands in or behind it, ground radars do not see into it, flying units are never hidden; without fog the map
    still shows everything but what stands in smoke. A guided missile whose target stands in a screen, or whose
    operator's line to it crosses one, loses guidance (flies on, drops). Charges refill where weapons rearm: a supply
    depot, the field ammo point, a garage's service (the military garage, the forward airbase's field garage).
  - **Queries:** `World.Smokes` (`SmokeCloud`: id, source, owner, centre, radius, since, until), `World.SmokeEvents`
    (`Popped`, `Expired`, `LockBroken` with the missile), `World.SmokeOf(unit)` (charges, max, ticks to ready),
    `World.SmokeAt(point)`, `World.InSmoke(unit)`; seats from `World.PassengersOf` and `Def.Crew.Passengers`.
  - **Hashing:** the screens once one was popped; a unit's `DismountWait`, `Dismounted`, `SmokeReady` only for units
    with smoke or a staged unload: games without them hash as before.
- **Main battle tank** (`main_battle_tank`, "Abrams", 2026-10-24; `tank_120mm`, `tank_mgs`, `trophy_aps`, `smoke_launchers`,
  the `apfsds` and `he_mpat` warheads; `Sim/Systems/ArmorSystem.cs`, `Sim/Systems/ApsSystem.cs`; `AbramsTests`; design:
  [docs/design.md](docs/design.md), "Main battle tank"). The Sentinel's M1A2 type: tracked, `armored_vehicle`, 2,500 HP,
  speed 30, radius 12, 2,500, 45 s at the military garage only (the vehicle garage's `production.excludeTags: ["tank"]`),
  a driver, no passengers, crusher 3 (soldiers and the light cars: `crushable` 2 on the dirt bike, technical, rocket jeep).
  - **Gun:** 40 rounds in one magazine, 20 cells, every 6 s, on the move. Two weapon effects in one component, the second
    with `sharedAmmo` (it draws on the weapon before it): high explosive (`he_mpat`, 250, 3-cell splash) and armour-piercing
    (`apfsds`, 900, 150 cells/s); the usual pick (damage per tick for the target's armour) takes the dart against vehicles
    and HE against soldiers and buildings; an attack-ground order fires HE (the first).
  - **Machine guns:** `tank_mgs` (`coaxial`): 12 a burst (`heavy_bullet`) at 7 cells every 0.25 s, ground only, unlimited, on
    their own reload (`Unit.CoaxReload`, `Unit.CoaxShot`) at the tank's target alongside the gun; picked as the weapon only
    when no other can fire (the gun empty or out of reach).
  - **Directional armour** (archetype `directionalArmor: { front, side, rear, top, frontArc, rearArc }`, opt-in): a hit's
    damage is scaled by where it lands against the hull's facing: 40% front, 80% side, 130% rear (90-degree arcs), 150%
    from above (lobbed rounds, drones, bombs, a flying shooter, a falling wreck). Queries `World.ArmorHits` (unit, facing,
    percent, damage, by), `World.FacingFrom`, `World.ArmorPercentFrom`.
  - **Trophy** (the `aps` effect: `{ charges, range, cooldown, chance, fastFrom, fastStep, fastPenalty, closeRange,
    closePenalty, minChance }`; the tank: 3, 4 cells, 1 s, 90%): engages a projectile of an `apsThreat` weapon (the RPG,
    the Kornet, the Kaman's, the helicopters' and the Watcher's missiles) aimed at its unit, a charge an attempt; the chance
    falls 10% per 10 cells/s above 25 and up to 40% for a launch under 20 cells; a seeded roll. A salvo overwhelms it.
    Refilled where weapons rearm. Queries `World.ApsEvents` (`Intercepted`, `Failed`, `Empty`, `Overwhelmed`),
    `World.ApsOf(unit)`, `World.ApsChance`, `World.ApsChanceAgainst`.
  - **Hashing:** a unit's `ApsReady`, `CoaxReload`, `CoaxShot` only with an APS or a coaxial gun; a projectile's launch point
    and APS look only in a match with an APS or directional armour: games without them hash as before.
- **Light tank** (`light_tank`, "Booker", 2026-10-24; `light_tank_105mm`, `tank_mgs`, `smoke_launchers`; the airlifter's `airlift`
  block, `Sim/Systems/AirliftSystem.cs`; `BookerTests`, `AirliftTests`; design: [docs/design.md](docs/design.md), "Light tank").
  The Sentinel's M10 type: tracked, `armored_vehicle`, 1,400 HP, speed 38 (the Abrams 30), turn 3, radius 11, vision 9 cells,
  1,500, 30 s at the military garage only, crusher 3, a driver, tags `tank` and `airliftable`. The Abrams' schemes: 35 rounds of
  HE (170) or the dart (600) by target, 18 cells, 6 s, on the move; `tank_mgs`; directional armour 60 / 100 / 140 / 160%;
  smoke (2 pops); no Trophy.
  - **Airlift** (archetype `airlift: { seats, tags, ramp, boardRange, boardTicks, unloadTicks }` on a runway aircraft; the
    airlifter: 2, `airliftable`, 11.5 cells, 4 cells, 10 s, 5 s): `Enter` (4) with the airlifter as the target, while it is
    parked at a station of a main air base, sends the vehicles to its ramp point (`World.AirliftRamp`); one at a time they go
    up it (`Unit.AirliftBoard`) and are aboard (`Unit.Inside`). The first takes its containers off: while a seat is in use
    (`Unit.Lift`) it loads none and refuses Airdrop. `Deliver` (39) to a forward airbase: at its stand they drive off 5 s
    apart to the field garage's rally point; `Exit` on it parked lets them out; destroyed, they are lost with it. Queries
    `World.AirliftEvents` (`Boarding`, `Aboard`, `PalletsOff`, `DroveOff`, `Lost`), `AirliftAboard`, `AirliftOf`,
    `AirliftDocked`, `CanAirlift`. Hashing: `AirliftBoard` and `Lift` only when not 0.
- **Sentinel cargo truck** (`sentinel_cargo_truck`, an 8x8 hook-lift truck, 260 HP, speed 22): the `cargo` effect of
  the cargo truck (400 supply, unloads into a friendly depot all at once in 3 s); it replaces `cargo_truck` in the
  Sentinel's roster and lobby (the Resistance keeps it). Built in either garage; refilled only by garage service.
- **Garrisonable buildings** (`garrison` on a structure archetype:
  `block_panel_5`, 23x6 cells, holds 10; `block_brick_4`, 14x7, holds 6; the town
  buildings `house_small` 5x5, holds 4, 700 HP; `villa_walled` 8x9, holds 6, 1000 HP;
  `shop_row` 7x6, holds 6, 1100 HP; `skeleton_frame` 8x6, holds 8, 1300 HP; and the
  worker-built `observation_post`, 5x4, holds 3, 1500 HP: `owned` (its builder's for good, only
  its side enters, never neutral; placed like any structure, not neutral), occupants out of its
  ruin at min(HP, 40%) for sure, `launcherTags` [sniper] (its abilities, the scout drone launch
  `scout_station_launch`, work only while a sniper is inside: `World.PostLauncher`), and a `highGround` vision bonus on the archetype: +5% per metre
  above the mean ground within 32 cells, capped at +60% (`World.SightRange`). Nobody enters a
  building before it is finished).
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
  fixed at launch (attack ground). Each one leaves its rail at `launchHeight` and climbs at up to
  `climbRate` (12 m/s) toward `cruiseAboveMax` (30 m) over the map's highest ground (on a flat
  map, 30 m), rounding off into level flight over `roundOff` ticks (the climb rate falls evenly to
  zero: no corner at the top). It cruises there at 12 cells/s, then pitches over: its slope turns
  evenly to the dive's `dive` (1428 permille, about 55 degrees) over `pitchOver` ticks of flight (2
  s), started so that the straight dive after the arc ends on the point; range up to 600 cells.
  All integer (`World.DroneNext`, a parabola in height over the arc), and the flight-path angle
  never turns faster than about 60 degrees a second. Its path is sampled 8 times a cell:
  - ground above it sets it off in any phase (a hill in the climb);
  - in the dive, the first structure (footprint, up to its archetype `height`), vehicle or
    soldier (its radius, up to its height) sets it off, else the ground at its point;
  - it cruises above everything.
  The projectile's flight (`Phase`, `Altitude`, the dive's start and slope, its last step, `Age`) is hashed, and it is
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
- **Kaman drone** (Kaman-22 type, 2026-10-22; `Sim/Systems/KamanSystem.cs`; `KamanDroneTests`): a long-range attack
  drone that is a flying unit (`kaman_drone`: a drone `flight`, air class `kaman`, and the archetype's `kaman` block:
  `damage`, `warhead`, `splash`, `splashCenter`, `splashEdge`, `dive`, `pitchOver`, `runway`). A weapon with
  `launches` (an archetype with a `kaman` block; instant, one round, ground targets) fires it on an attack (a unit: it
  follows it) or attack-ground order: the Kaman truck's `kaman_rail` (`kaman_truck`, 60 to 1,200 cells, reload stowed at
  a depot 60 s / 400) and the engineer's Kaman station (`kaman_station`, placed with `kaman_station_kit`). In flight:
  150 m up, 15 cells/s; a target its side stops seeing is flown to where it was last seen; 2 fire-and-forget missiles
  (`kaman_missiles`) from 30 cells a second apart; a 45-degree dive into the target: 520 through `kaman_charge` in 6
  cells (armour dies, a building loses 45%). 2 Igla hits or 1 air defence missile bring it down. State `Unit.Kaman`
  (`KamanState`), events `World.KamanEvents`. See `docs/design.md`, "Kaman drone".
- **Mobile radar** (SA-6 "Straight Flush" type, 2026-10-22; `Sim/Systems/RadarCueSystem.cs`; `MobileRadarTests`): the
  Resistance's `mobile_radar` (a light tracked vehicle, 280 HP, speed 30, 1,600, 40 s at the vehicle garage; no
  weapon). It searches the air only set up (Deploy 8 s, Stow 6 s; moving packs it up): `airVision` 400 cells, slant
  from its antenna 7 m up, by each target's signature, feeding its side's team cueing (`World.AirTracked`) and, under
  fog, its side's sight of air units. The archetype's `cueLock` (70) is the radar-cued lock: a ground air defence
  weapon of its side whose target a set-up cueing radar tracks locks in that percent of its own lock time (the AA
  soldier 1.5 -> 1.05 s, the air defence vehicle 1 -> 0.7 s); never more range, the same kill chance. Off for every
  other unit (the radar truck included). For renderers: `World.RadarOf` (with `CueLock`), `RadarTracks`, `RadarSweep`
  (`sweepTicks`: the antenna's turn), `RadarEyeRaw`, `RadarCuedShooters`, `RadarCued`, `AirLockTicks`. See
  `docs/design.md`, "Mobile radar".
- **SA-6 launcher** (2K12 Kub type, 2026-10-22; `Sim/Systems/BatterySystem.cs`; `KubLauncherTests`): the Resistance's
  `kub_launcher` (tracked, 280 HP like the Osa, speed 30, 2,000, 45 s at the vehicle garage) with 3 `kub_missiles`
  (350 cells of slant, 55 cells/s, about 1.3x the Osa's kill chance and share of HP a hit, capped). A separated battery: its
  interceptor's `battery` block (`linkRadius` 60 cells, `radarTags` `radar`, `semiActive`) makes it fire only at what a
  set-up mobile radar of its side within 60 cells tracks, and its semi-active missile loses its guidance (and misses) the
  tick no radar linked to its launch point tracks the target any more. Sets up 5 s, packs up 4 s; reloads only at a
  depot, packed up, one missile every 20 s for 200 (not packing up while it tracks). For renderers: `World.BatteryRadar`,
  `BatteryLaunchers`, `BatteryOf`, `BatteryLost`. See `docs/design.md`, "SA-6 launcher".
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
- **Mortar soldier** (`mortar`): a light mortar (`mortar_tube`, warhead
  `mortar_bomb`), indirect fire from 8 to 30 cells on a high arc (`arc` 45:
  no line of fire needed). Deploys in 5 s and stows in 3 s like a launcher
  (`deployTicks` on an infantry archetype), lays the tube 1 s (`aimTicks`)
  before every round, one round every 4 s, 10 bombs refilled only at a
  depot. Its scatter grows with the distance: the weapon field `scatterNear`
  is the scatter at the minimum range, rising linearly to `scatter` at full
  range (`World.ScatterAt`). Attack ground fires at any point in range,
  spotted or not. `World.ShotAt(unit, point)` tells a client whether a point
  is in range, too close or out of range (the fire-radius display). Inside a
  building he fires only from an observation post: a garrison's `deployTags`
  lists the tags of units that must set up (`deployTicks`) yet may fire from
  inside (`World.FiresFromHere`); elsewhere they hold fire.
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
- **Stances** (`SetStance` command, `stance` field; `Unit.Stance`, hashed when not Guard; the
  archetype's `stance` sets the start): Guard (the default: engage spotted enemies in reach, and
  retaliate on the leash), Aggressive (also hunt what is spotted within vision x 1.33), Hold ground
  (engage in reach, never move: Guard before the unit AI), Return fire (only the enemy that last
  hurt it, `Unit.LastHitBy`) and Hold fire (never fire on its own; orders are obeyed).
- **Unit AI** (`Sim/Systems/UnitAiSystem.cs`; rules: `docs/design.md`, "Unit AI"; numbers: the
  `unitAi` content block, `content/mvp/unit_ai.json`). An enemy hurting a unit in Guard or
  Aggressive sends it and its idle friends within 8 cells after the shooter (`Unit.Ai` Engage), if
  their archetype pursues (`pursue`), it stood within 20 cells and they can hit it; the chase runs
  on a leash (vision x 2.2) from where each stood (`Unit.GuardPoint`) for at most 300 ticks, then it
  takes on what is spotted within vision x 1.8 or walks back (Return). A shooter it can't see: it
  heads for where the shot came from (`Unit.LastHitFrom`). `AttackMove` (stop for targets in reach,
  turrets fire on the move, carry on), `Guard` (a cell, each of a group its own slot, or a unit:
  defend the circle, chase, return) and `Scatter` (away from the group's centre by 4 x radius, at
  least 2 cells) are commands 24-26. An Attack target lost under fog becomes an attack-move to where
  it was last seen. Targets picked on their own: the attacker, threats, then the old order; unarmed
  structures only on orders. A set-up mortar fires on its own (`autoFireDeployed`); other launchers
  that deploy only on orders. Vehicles with `crusher` above a unit's `crushable` run over enemy
  infantry they drive into (`World.Crushes`), at the start of the combat system. Tests:
  `StanceTests`, `RetaliationTests`, `AttackMoveTests`, `TargetPriorityTests`, `ScatterTests`,
  `CrushTests`, `SkirmishAiTests`.
- **Air defence soldier** (`manpads`, `igla`): detects one-way attack drones (projectiles marked
  `interceptible: "drone"`) within his `airVision` (150 cells, slant), tracks one in Guard (idle,
  standing), and after `lockTicks` (1.5 s) of tracking fires a 3D guided missile (30 cells/s,
  45 degrees/s, 180 cells of flight) from 8 to 140 cells. It flies lead pursuit: it steers at the
  drone's position plus its velocity times the time to go (at most `leadTicks`, 4 s). The 3 m
  proximity fuse checks their closest approach over each tick with both moving. Then a kill roll
  decides: `Rand.Hash(seed, tick, shooter, target)` against a chance by aspect (78% tail-on, 42%
  head-on), target speed, crossing rate and slant range at launch, and tracking time. A kill removes
  the drone in the air; a missile that can't reach its target self-destructs at max flight.
  `World.AirBursts` lists each tick's outcomes with the chance and the roll. 5 missiles (1 loaded,
  4 spare), 4 s to load the next, refilled only at a depot. Design: the game repo's `docs/air-defense-design.md`.
- **Missile defence** (the Sentinel's `patriot_launcher`, `radar_truck`, `patriot_pad`; design: the game repo's
  `docs/patriot-design.md`; `PatriotTests`; bench `SimRun -bench-sam N`). The launcher's interceptor is `radarLinked`: no
  sensor of its own, it engages only what a friendly radar (a unit with a `groundRadar`: the radar truck, a radar site, the
  air base's radar) tracks within its reach, and only set up (`deployTicks`; the radar truck senses only set up too). Arcing
  projectiles marked `interceptible` (`missile`: the twin missile; `rocket`: the heavy rocket truck's and the jeep's;
  `ballistic`) are air targets on their arc (`World.OnArc`, `ArcStep`: height and velocity from the arc, on flat maps too).
  Per-class `salvo` (2 at a `ballistic`: how many fly at once) and `defendRadius` (an arcing threat only if it comes down
  within 15 cells of its side). `maxPerTarget` (the high-altitude `thaad_launcher`'s: 2) caps how many of its side's capped
  missiles are launched at any one target over its life: a target at the cap is never picked, so it moves on
  (`Projectile.CapShots` / `Unit.CapShots`, per owner, hashed when set). A structure's `reloadPad` (tags, `reloadTicks`, `airfieldRange`) refills a packed-up tagged unit standing on it, one at
  a time, placed and working only near a standing friendly airfield (`Unit.PadUnit`, `PadLeft`, hashed when set).
- **Strike drone** (the Sentinel faction's `watcher_drone`, launched by its `watcher_station`, the Watcher control station; the Overseer has its own `overseer_station`: one drone type per station;
  `Sim/Systems/WatcherSystem.cs`; design: `docs/design.md`, "Sentinel faction", and the game repo's
  `docs/watcher-design.md`): a flying unit with a `flight` block (it orbits like a scout) and a weapon
  with an `airStrike` block, fired only on an attack order at an enemy ground unit within the weapon's
  range of the point under it. The hit is rolled at launch (one `World.Rand` draw: `hitIdle` percent if
  the target did not move last tick, `hitMoving` if it did); the missile flies a straight 3D line from
  the drone's height, homing on a hit, else to a point `missNear`-`missFar` off (behind a moving
  target). A hit destroys a unit with a `kills` tag, except one with a `heavyTags` tag, which a second
  draw destroys `heavyKill` percent of the time (else it keeps at most `heavyHpLeft` percent of its HP);
  anything else takes the warhead's damage; then the splash. Its ammo has `noResupply`; empty, it
  orbits on (the flight's `homeWhenEmpty` is deprecated and ignored: only a Rearm order sends it to
  rearm); a `relaunch` drone is recovered only once down on its station's apron, and the station
  relaunches it to its old orbit after the turnaround, with the stocks and HP it came home with unless
  a Rearm order sent it (`Unit.RelaunchPending`, `RelaunchCell`, `KeptStocks`, hashed when set). Air defence engages it as the `watcher`
  interception class (`killBest.watcher`). `World.StrikeReports` lists each tick's launches (with the
  roll) and impacts (with the outcome). `WatcherTests`.
- **Footprints.** A non-square structure built facing East or West stands turned a quarter:
  its width and height swap (`Unit.Turned`, `World.FootW` / `FootH`, read everywhere a
  footprint matters). Archetypes laid out from their unturned top-left cell (factory doors,
  garrison doors, tunnel portals) can't turn; spawned structures keep their layout.
  `FootprintRotationTests`.
- **Airfield (M1)** (`Sim/Systems/AirfieldSystem.cs`, `Sim/Airfield.cs`; `AirfieldTests`,
  `AirfieldContentTests`): a worker's `Build` of a facility founds an `Airfield`
  (`World.Airfields`, ID = the facility's) whose frame turns with the facility's facing; it
  reserves its first block's plan (the minimum runway and row (0, 0)) against every player's
  structures (`World.AirfieldPlan` for the ghost). With the facility standing, `Build` of a row
  or runway segment snaps to the type's grid (`World.AirfieldRowCell`, `AirfieldSegmentCell`):
  a row touches the airfield and has the row below it, its lane mouth over the taxiway; the
  first row lays the minimum runway as passive sites on its one bar (`Unit.BuildTotal`);
  segments extend either end while the approach corridor stays on the map. Every piece is
  flattened to the airfield's one pad height. A finished row spawns its stations; their
  records hold their kind, occupant and reservation. `World.SpawnAirfield` places a finished
  airfield (scenarios, tests, the sandbox's facility); `World.StationsFor` and
  `World.AirfieldTaxiGraph` derive what the landing rules (M2) will use. Pavement, open stands
  and runway segments are never targets and never damaged. The airfield records and the pieces'
  `Unit.Airfield` are hashed only when an airfield exists. Flight, landing, taxi, service,
  rubble, repair and craters are not built yet (M2, M3).
- **Airfield (M2)** (`Sim/Systems/AirfieldFlightSystem.cs`, `Sim/AirOps.cs`;
  `AirfieldFlightTests`): an aircraft (flight and runway `aircraft` blocks) whose home is the
  nearest own airfield serving its class with a free station and a long enough open runway
  holds there (stacked by its landing-queue place), lands one at a time (the airfield's
  `RunwayLock`), rolls out, taxis on rounded paths over the taxi graph to the station it
  reserved, parks (fuel does not burn on the ground; M2 refuels at 1x), and takes off again the
  other way, keeping its ID and HP; with none it uses its control station as before.
  `Unit.AirOps` (phase, station, speed, path), `World.FlightPhaseOf` and
  `World.AirfieldEvents` are for renderers. See `docs/design.md`, "Airfield (M2)".
- **Airfield (M3b)** (`Sim/Systems/AirfieldServiceSystem.cs`; `AirfieldServiceTests`):
  parked aircraft are refuelled, rearmed and repaired at their station's rates (a loading
  station 2x rearm and repair, nothing while the facility is down; a stand 1x), and a sortie
  aircraft leaves once serviced; `CommandKind.ReturnToBase` (28) recalls aircraft; on the
  ground an aircraft has its `groundArmor` (`parked_aircraft`), sees as a ground unit and air
  defence ignores it; craters send it around, stop it rolling (or hurt it) and divert it.
  `World.ArmorOf`, `World.ServiceOf` and `World.StationWorks` are for views. See
  `docs/design.md`, "Airfield (M3b)".
- **Main air base and radar** (H1; `Sim/Systems/AirfieldSystem.cs`, `Sim/Systems/AirBaseSystem.cs`;
  `MainAirBaseTests`): an airfield type may have a `core` (`pieces.core`, `hangar`, `radar`, `heavy`; the
  `core` layout block; `rows` origin and caps; `runway.connectorEvery`; `facilityDown`; `link`): its core is
  placed as one unit and, finished, lays the command building (the facility), the hangar with its heavy bays
  (stations of kind `heavy`, rows with a negative column), the radar, the runway and the built-in rows.
  Stations may limit classes (`minClass`, `maxClass`). A structure's `groundRadar` spots enemy vehicles
  anywhere and infantry in the open within its reach; rubble sees nothing. `World.SpawnAirBase`,
  `World.HeavyBays`, `World.FreeHeavyBays`, `World.AirfieldLinks` (a linked forward airfield joins the base's taxi
  graph), `World.AirfieldLinkTaxiways`, `World.RadarOf`. See `docs/design.md`,
  "Main air base and radar (H1)".
- **Ground attack jet** (H2; `Sim/Systems/AttackPlanSystem.cs`; `AttackJetTests`): `attack_jet` (class L, the
  `attacker` air class; built by the main air base's hangar, parked in a heavy bay) with 8 `heavy_bombs` (tier 6,
  1.5x the 2,000 lb blast), its heavy nose gun and 2 light IR missiles. Bomb fire modes per aircraft
  (`Unit.FireMode`, `CommandKind.SetFireMode` 32 with `index`; `World.FireModesOf`); Shift (`queue`) on Attack,
  AttackGround and `CommandKind.AttackLine` (33: `cell` to `cell2`, the bombs spread along the line) appends to the
  aircraft's queue of aims (`Unit.Aims`), close ones bombed in one pass; a pass's plan is on its `Unit.Strike`
  (`Planned`, `Forced`, `Entry`, `Drops`, `DropNext`, `Served`). Generic for any aircraft with free-fall bombs. See
  `docs/design.md`, "Ground attack jet (H2)".
- **Gunship** (H3; `Sim/Systems/GunshipSystem.cs`; `GunshipTests`): `gunship` (class XL, the `gunship` air class: 8
  missile hits; built by the main air base's hangar, parked in a heavy bay; 360 m up, above the AA soldier's
  ceiling). An Attack-ground (a cell) or Attack (a ground unit) order is an orbit attack: a left-hand pylon turn round
  the area (`Unit.Gunship`), its three side cannons (100 light, 20 heavy, 1 very big round) firing only from the
  circle at what its side sees in the area, scattered (`World.SideFires`; impacts in `World.BombImpacts` with `Shell`);
  out of rounds it goes home. Also `World.RadarTracks(radar)`. See `docs/design.md`, "Gunship (H3)".
- **Strategic bomber** (H4; `Sim/Systems/AttackPlanSystem.cs`; `StrategicBomberTests`): `strategic_bomber` (class XL,
  the `bomber` air class: 6 missile hits; built by the main air base's hangar, parked in a heavy bay; 400 m up, above
  the AA soldier's ceiling; 30 min of fuel) with 20 `strategic_bombs` (tier 7) dropped level from altitude, released
  about 50 cells before each drop; fire modes `carpet20` (the default: all 20 in one stick, spread along a line or a
  group) and `salvo4`; Shift queues and AttackLine as the attack jet's; it overflies its drops before it breaks; no
  air-to-air weapons; 30 flares. See `docs/design.md`, "Strategic bomber (H4)".
- **Supersonic bomber** (B-1 type, 2026-10-22; `Sim/Systems/AttackPlanSystem.cs`; `SupersonicBomberTests`):
  `supersonic_bomber` (class XL, the `bomber` air class; built by the main air base's hangar, parked in a heavy bay;
  400 m up; cruise 18 cells/s, 22 on its run; 25 min of fuel) with 24 `supersonic_bombs` (1,400, tier 6) dropped in
  **rows across its flight path** (a fire mode's `across` and `acrossSpacing`, generic): `row4` (the default: a row of 4
  at each aim, 9 cells wide) and `block24` (all 24 in one pass: 6 rows of 4 stacked along the path, about 9 x 15 cells;
  spread end to end on a line). A pass's rows: `StrikeRun.Across`, `DropSide`, `DropPoint(i)`; each bomb's landing in
  `World.BombReleases`. No air-to-air weapons; 30 flares; home to rearm when every bomb is spent. See `docs/design.md`,
  "Supersonic bomber".
- **Stealth bomber** (B-2 type, 2026-10-22; `Sim/Systems/AttackPlanSystem.cs`, `PenetratorSystem.cs`,
  `ExclusiveSystem.cs`; `StealthBomberTests`): `stealth_bomber` (class XL, the `stealth` air class: 3 missile hits; 700 m up;
  signature 25 / 300 / 70: a quarter of every sensor's range, so never seen in flight at 700 m; cruise 15 cells/s) with two
  weapons the player picks between (`CommandKind.SetBombWeapon`, `Unit.BombWeapon`, `World.BombWeaponsOf`; each its own fire
  mode): 16 `stealth_bombs` (precision: no scatter, each bomb steered onto its own point; `all16` a 4 x 4 grid / `group4` a
  2 x 2; up to 16 Shift-queued aims, one bomb each) and 2 `bunker_busters` (`both` / `single`; `weapon.penetrator`:
  everything within 3 cells destroyed whatever its armour, every unit in a tunnel node there killed, the entrance collapsed;
  a deep crater: `CraterKind.Deep`, `World.Craters`, `World.TunnelKills`, `World.TunnelCollapses`). The heavy bombers'
  exclusive group (`exclusiveGroups`, archetype `exclusive`; `World.ExclusiveOf`, `Player.ExclusiveLocks`): one at a time,
  the type locked by the first built. See `docs/design.md`, "Stealth bomber".
- **Logistics** (2026-10-22; `Sim/Systems/AirdropSystem.cs`, `LogisticsSystem.cs`, `SupplySystem.cs`; `LogisticsTests`): the
  `airlifter` (C-5 type, class XL, unarmed) with 4 containers of 2,500 supply (`cargo_pallets`, weapon `airdrop`), loaded free at
  the main air base; `CommandKind.Airdrop` (38: a low pass over a drop zone, fire modes `all4` / `single`, pallets scattered,
  lost outside the zone) and `Deliver` (39: it lands at a forward airbase and unloads into its stock); the `facp` (field ammo point:
  a stock that rearms its side's ground units within 12 cells), the `supply_stash` (storage), and the `forward_airbase` (a compact
  core-founded field: 7-segment runway, 2 stands for any class, a 2-bay field garage, no production, a limited stock every
  service there draws on, at least 120 cells from the main air base: content `awayFrom`). Cargo trucks `LoadCargo` (40) at a
  store whose supply is a `cargoSource`. New content fields: supply `startStock`, `storage`, `cargoSource`, `groundOnly`,
  `roundCost`, `repairCost`, `fuelCost`; archetype `dropZone`, `awayFrom`; weapon `airdrop`; production `bays` 0 (doors for a
  service block only); airfield `rows.extra`; a core type's radar is optional. Queries: `World.LogisticsEvents`, `StockOf`,
  `DropZoneOf`, `CargoAboard`, `AirfieldStore`. See `docs/design.md`, "Logistics".
- **Helicopters** (2026-10-21 night; `Sim/Heli.cs`, `Sim/Systems/HeliFlightSystem.cs`, `HeliCombatSystem.cs`,
  `HeliPadSystem.cs`, `HeliCommandSystem.cs`; `HelicopterTests`; design: `docs/design.md`, "Helicopters"): the
  Sentinel's `attack_helicopter` (AH-64 type: chin cannon, 8 anti-tank missiles, 2 rocket pods, a mast radar),
  `armored_helicopter` (Mi-28 type) and `light_helicopter` (AH-6 type: miniguns, 8 light rockets, 3 riders) and the
  `helipad` (12 x 12, built by a worker or the bulldozer; builds one helicopter onto itself, parks and services it). The
  rotary flight hovers, yaws on the spot, flies sideways and backwards, follows the terrain 20 to 60 m up and lands
  straight down (`Unit.Heli`). Missile attacks from cover: it hides behind a crest or woods, pops up to fire, ducks
  (`HeliAttack`); guns and rockets: runs that sidestep round the target. Brought down it goes **out of control**: its HP
  drains over 30 s while it flies itself to the nearest pad (a hard landing it survives), else it crash-lands (a wreck,
  crew and riders roll survival); a further hit, or a Patriot-type hit, destroys it in the air. Orders: Move, Attack,
  Attack-ground, Attack-move, Guard, Stop, AirTask Patrol, ReturnToBase, Rearm, Enter (riders), Exit,
  `CommandKind.HeliLoad` (35), `HeliUnload` (36). Queries: `World.HeliStatusOf`, `PadStatusOf`, `RidersOf`,
  `HeliCrashes`, `AirStatusOf`; bench `SimRun -bench-heli N`.
- **Air combat** (E2 to E11; design: `docs/design.md`, "Air combat"). Every aircraft flies a
  mission (Orbit, Patrol, Guard, Search, Roam, a Strike on top; `CommandKind.AirTask` 30) through a
  flight controller (edge avoidance first) on its kind's kinematics (the drone law, or the jets'
  fixed-wing energy envelope); only Guard picks targets. It goes home on its own only at bingo
  fuel (refuel only, then it resumes), rearms and repairs only on `CommandKind.Rearm` (29), and
  ReturnToBase (28) refuels and holds it (`Unit.HomeReason`). Sensors detect within `airVision` (a
  slant distance) x the target's signature and cue their side; AA and jets fire missiles (lock,
  lead pursuit, 3D fuse, a seeded kill roll x signature x countermeasures) and guns; jets bomb
  (level, dive, toss, guided: an exact integer release solution) and strafe; a gun kill makes the
  pilot eject (`World.Chutes`), any loss in the air leaves a falling wreck (`World.Wrecks`). Jets
  fly the overhead break and take off on military power or afterburner; each aircraft uses only
  fields whose pattern fits the map (a jet's `corridorNeed`, a drone's straight-in join).
  `World.AirStatusOf` and the per-tick event lists are for renderers. Tests: `AirRulesTests`,
  `AirMissionTests`, `FixedWingTests`, `AirClassTests`, `JetTests`, `AirToAirTests`,
  `AirToGroundTests`, `LossTests`, `JetAirfieldTests`; bench `SimRun -bench-air N`.
- **Drones** fly straight to a unit (following it) or a cell and explode on
  contact; AA missiles down them in one hit, bullets do 15%.
- **Traps** are hidden from the enemy until an enemy detector (a sniper, a
  scout drone or an observation post; `detect`) has them in reach and, under
  fog, in sight (revealed per player, for good); then anyone can shoot them.
  They fire once on an enemy ground unit, never on drones; a placer's
  `maxActive` caps how many one soldier keeps (the next removes the oldest).
  Their owner sets them off on demand (`Detonate`) or turns contact off on a
  trap (`SetAutoDetonate`, `on` field; `Unit.AutoOff`); a trap going off lights
  every trap within its blast, which goes off `chainTicks` later (`Unit.Fuse`;
  both hashed only when set). A trigger with `kills` tags (the anti-tank mine)
  destroys one such unit outright and never kills a second. Rules: the "Traps"
  section of `docs/design.md`. `World.IsVisibleTo` is the renderer's "can
  player X see this" query.
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
tunnels, and networking. `builder` and `buildAssist` effects, and aura
`modifiers`, load but nothing runs them yet.

## Design

The game this engine is built for is designed in [docs/design.md](docs/design.md). [docs/resistance-mvp.html](docs/resistance-mvp.html) is the illustrated MVP walkthrough (it predates the move from sprites to real-time 3D); download it and open it in a browser.

## License

MIT. See [LICENSE](LICENSE). This repository is the engine only; game art and content live elsewhere.
