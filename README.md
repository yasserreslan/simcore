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
`S` sniper, `M` the Sentinel's assault medic (the Resistance's medium launcher elsewhere), `B` dirt bike, `T` technical, `Y` the Sentinel's Humvee, `I` the Sentinel's Abrams tank, `l` the Sentinel's Booker light tank, `Z` the howitzer, `L` / `M` / `H` light, medium and
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
`HASH_COMPARE=1` also checks the optimized hash against its original implementation on every
measured tick; the extra reference call is outside the reported tick/hash timings.
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

`-bench-squads N` runs the squad bench: N squads of 10 infantry (half March, half Assault) marching 150 cells across a flat
256-cell map and attack-moving back, against the same soldiers in plain group moves. Its gate is 50 squads under 1 ms a tick:
the squad pass took 0.09 ms of tick p99 and the path system less than with group moves (`bench/squads_50.json`, M3 Pro,
2026-10-28).

`-bench-hold N` runs the Hold bench: N squads of 10 on a hilly, wooded 256-cell map with low rock walls given Hold where they
stand, then a Hold 16 cells on (a march, then the picking), against the same squads given plain moves. Its gate is 50 squads
under 1 ms a tick: the squad pass took 0.60 ms of tick p99 (at most 2 squads pick a tick), and one order's scoring (441 cells,
10 members) 0.23 ms (`bench/hold_50.json`, M3 Pro, 2026-10-28).

`-bench-ambush N` runs the Ambush bench: the Hold bench's map and squads, each setting an ambush on a 20-cell kill zone next to it
(both sides scored, mines laid, Ready), then a technical of the other side driving down each zone (it springs), against the same
squads given no ambush. Its gate is 50 squads under 1 ms a tick: the squad pass took 0.48 ms of tick p99 and the path system 0.12
ms more (one side scored a tick), and one order's picking (both sides, 724 cells) 0.51 ms (`bench/ambush_50.json`, M3 Pro,
2026-10-28).

`-bench-air-ambush N` runs the air ambush bench: the Hold bench's map, N squads of 10 (6 Iglas, 4 riflemen) each setting an air
ambush on a 36-cell corridor beside it (the Iglas spread under trees, Ready), then jets of the other side flying over every second
row (the ambushes spring, the Iglas fire together), against the same soldiers walking to the same cells on plain moves. Its gate is
50 squads under 1 ms a tick: the squad pass took 0.33 ms of tick p99, the air defence 0.25 ms more and the path system 0.04 ms
more (gate 0.61 ms), and one order's picking 0.15 ms (`bench/air_ambush_50.json`, M3 Pro, 2026-10-28).

`-skirmish TICKS` plays the skirmish AI against itself (the battle lobby plus 4 Iglas and a mortar a side, on a 128 x 96 map with a
highway, two crossing roads and woods; `-map flat` for the battle map), once without squads and once with them, and prints the AI's
think time per decision, what its squads did (formed, ambushes set and sprung, Assault switches) and the outcome.

`-player-ai content/ai/player.json -setup content/setups/player_ai.json -fog true -ticks 2400` runs the new
host-owned player AI: observed-threat assessment, persistent role-aware forces, coordinated operations,
aircraft and transport jobs, service/repair, adaptive production, construction, crews and cargo delivery.
`-save FILE` records commands; `-ai-trace FILE` writes planner views. Pair `-ai-state FILE` with a replay
for checkpointing and `-ai-resume FILE -replay FILE` to continue. The demo lobby adds workers.
See [PlayerAi rules and integration](docs/player-ai.md) and [the open-source RTS AI research](docs/rts-ai-research.md).
Existing bots stay separate. The expanded foundation is compile-checked; match quality and checkpoint
continuation still need behavioral validation.

`-move-bench all|idle10k,moving10k,group500,group2000,pass,forest,depots` measures local movement
at game scale: up to 10,000 units on a 1,500 x 1,500 map (`-map flat`, or a map made by the
game repo's `tools/terrain/make_heightmap.py --size 1500 --bench-layout --out DIR/map`, which
also writes `map_layout.json`: a mountain ridge cut by three passes, massifs, graded winding
roads, forests). A bench driver stands in for routing (one flow field per group toward
formation slots); it reports tick and movement time, max overlap, stuck counts and arrival.
The `depots` workload adds 300 hard-box structures and 3,000 moving units crossing their lanes.
Results: `bench/move_1500.json`, `bench/move_flat1500.json`.

`-terrain-bench N|WxH|map.json [-battle N] [-fog true] [-prepare true] [-maxticks N]` loads a terrain (a synthetic
one of N x N or W x H cells: hills, noise, a forest map, three roads, a river into a lake) and reports load time,
digest time and memory; with `-battle` it also runs a world on it: bare ticks, the hash before and after a terrain
change, every class's regions (or with `-prepare` every sector), then N units fighting at the far corner while a
technical drives corner to corner, with tick p50/p99/max, the slowest tick's systems and the memory held. Results:
`bench/terrain_1024*.json`, `bench/terrain_6000x3000*.json` (docs/design.md, "Map size").

`-zuberec-bench DIR -town FILE [-battle 200] [-maxticks 2400] [-fog true] [-scenes ...]` runs a real map with a town
file (the game's Zuberec: 2,000 x 1,500 cells, 995 neutral buildings; not in this repository) as the client makes it,
the demo's convoy and a battle round the square, with and without the town, and reports the tick's and every phase's
p50/p99/max/mean. Results: `bench/zuberec_*.json` (docs/design.md, "Town performance").

`-spike-bench move,land,field,sectors [-map DIR/map.json] [-prepare true] [-fog true]` times single
ticks, each workload four times in one process (the first cold): `move`, 38 crewed technicals
given one Move order after 20 s on a 256-cell map; `land`, a gun-killed jet's pilot landing among
38 enemy technicals; `field`, the tick after an airfield is laid finished (`World.SpawnAirfield`,
its ground levelled); `sectors`, what building each routing class's HPA* sectors costs. It prints
the worst ticks with their slowest systems.

`-stuck-bench 1,2,3,4 [-map battle|flat] [-ticks 12000] [-out FILE]` runs reproducible movement QA:
small seeded groups cross two mixed armies, with empty vehicles and deployed launchers as obstacles.
Hold-fire keeps the armies alive. It counts units improving less than half a cell in a rolling 600-tick
window, once per order, and reports nearest-forward blocker causes and the worst ten positions.

### Movement

`Sim/Systems/MovementSystem.cs` moves every ground unit standing on the map along its
`MoveIntent` (written by the path system): terrain speed per movement class (slope up or down,
forest, road kind), avoidance (dodging, keep right when oncoming, separation in same-direction streams),
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

Distance vision keeps phase-local pending candidates per viewer side and bucket: once a target has
that phase's spotting deadline, another viewer of the same side can omit it. The lists reset on
every index build; target acquisition continues to use the complete index and exact priority rules.
`PerceptionEquivalenceTests` compares this path against the original indexed searches in dense
terrain battles with and without fog.

Supply uses a separate spatial window rebuilt after production when at least eight suppliers are
active. Nearby recipients remain in ID order without duplicates, so scarce stock is assigned exactly
as in a full scan. Smaller supplier counts retain recipient scans; depot lookups use the current,
ID-sorted supplier list. `SupplyIndexTests` compares both paths tick by tick. For same-binary checks,
`MOVEBENCH_SUPPLY_FULLSCAN=1` selects the original scans in the movement benchmark.

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
  table. `null` means the warhead cannot target that armor class. `maxHpPercent` makes a hit take a share of the target's max
  HP instead, and `maxHpBySplash` lets that share fall off with the splash (the incendiary bomb); `fire` is what the blast sets
  on fire (its `ground` ticks: open ground burns too).
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
  `lock` / `kill` percents), `loadouts` and `loadoutSwitch` (loadout modes: per mode its weapons' rounds by component, a speed
  percent and a signature; changed parked, `CommandKind.SetLoadout`), `radarSpotting` (`tags`, `linger`, `radius`: enemy air
  radars whose coverage it flies in are revealed to its side), `countermeasures` (flares, chaff: salvos per sortie, refilled on a
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
  degrees off the nose), `guided.fireAndForget` (its own seeker), `guided.lockAfterLaunch` (HB, with `fireAndForget`:
  at a vehicle the helicopter's own mast radar tracks it fires from cover and lofts over the crest), `ripple` (HB:
  `targets`, `interval`, optional `cone`: one round at each of up to `targets` different vehicles, `interval` ticks
  apart, the later ones within `cone` degrees of the nose, then the reload), `intercept.outright` (a hit destroys a helicopter outright); an archetype's `groundRadarVehicles` (a ground radar that spots vehicles only: the mast radar), `cueLock`
  and `sweepTicks` (the mobile radar: its side's ground air defence locks faster on what it tracks; its antenna's turn);
  an interceptor's `battery` (`linkRadius`, `radarTags`, `semiActive`: the SA-6 launcher fires only on a linked radar's
  track, and its missile needs it to the fuse); an air sensor's `counterStealth` (a percent: every signature value moves
  that far toward 100 against it, and its battery launchers lock and kill by it), `radarHorizon` (`beyond`, `below`: low
  targets far out hide behind the ground) and `emitter` (`range`, `radius`, `linger`, `excludeTags`: its searching radar
  is revealed to enemy radars; the Hide stance switches it off), the S-400 type radar's; a structure's
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
  paths, heading, pushback), holding (with `reach`, how near the hold point the next is cleared to land), lock and
  taxi-wait times, collapse damage, the damage of running into a crater (`craterHitDamage`), the crater and wear numbers
  and its `tempo` (`leave`, `return`: percents of the aircraft's own pace it leaves and comes home at; 2026-10-24, 130 and
  150; a helicopter's `rotary.tempo` likewise for its pad). Each piece archetype has an `airfield` block naming its `role`
  (stations add `kind`, `needsFacility`, `rearm` / `repair` / `refuel` percents and
  `maxSpan`). Structures may set `bridge` (`road`, `stages`, `reopen`, `helpers`: a bridge the map places over water;
  README "Bridges"), `pavement` (no route or movement block; road on terrain),
  `targetable: false`, `invulnerable`, `neutral` (no garrison; placed neutral for good), `rubble.hpPercent` and
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
  their blast does to a runway (`runway`: a `crater`, `small` or `large`, or `wear`). A weapon may need an `upgrade` (a name:
  without it the unit's stock of it holds nothing; the incendiary bomb's "incendiary") and a bomb may be `pointsOnly`
  (dropped on ground points only, never at a unit). A bomb's `delivery` may fall `straight` (no forward throw: released over its
  point) and `warn` every enemy from its release; its `blast` (the MOAB type bomb's) deals damage by `rings` (radius, percent at the
  inner edge, `edge` percent at the outer edge, `destroys`) instead of the splash falloff, collapses every tunnel within `tunnels`
  and flattens the forest within `flatten` down to `flattenTo`.
  Everything is checked against the type (footprints, slots, roles) when content loads.
- **Match setup** (`content/setups/battle.json`, a Resistance mirror;
  `content/setups/sentinel.json`, Sentinel against Resistance): per player a faction,
  named presets (an archetype and one component per slot, in slot order) and
  a starting army (presets or slotless archetypes, with an optional driver
  per vehicle), and optionally the `upgrades` it starts with (none in the shipped lobbies). It is checked against the content
  when the match starts,
  pinned to the content hash and seed, stored in the replay and hashed into
  every world hash. Machines with different content refuse to start.

The content hash is a SHA-256 over the compiled, ID-sorted definitions, so
file layout, order, whitespace and line endings don't change it.

Systems run every tick in this order: commands, vision (spotting, then trap
detection), orders (with the unit AI), boarding vehicles, abilities (traps and drones), ground
movement, air movement, combat (crushing first), air defence, projectiles, drone strikes, trap triggers,
supply, repair, auras, lifetimes, tunnels, airfields (with structure repair), bridges, driver ejection, building collapse,
cleanup, garrisons (stages, capacity, ownership), match result.

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
- **Concealment from the air** (2026-10-25; `content/mvp/concealment.json`, `Sim/Systems/ConcealmentSystem.cs`;
  `docs/design.md`, "Concealment from the air"). With fog on a map with terrain, a soldier or vehicle standing in a grove or
  dense forest is not spotted by the air units' discs: each air unit's `sensors` decide, in the vision system's distance pass.
  A camera (every air unit's default) sees it only within the table's reach for its kind, whether it moved last tick and the
  density (a still soldier in a grove within 3 cells, in dense forest never; vehicles twice as far); thermal (the Overseer,
  the Watcher, the gunship) sees vehicles that moved in the last 30 s and infantry walking in groves; ground radar (the stealth
  multirole's heavy mode) moving vehicles; a shot shows the shooter to every air sensor for 5 s; a low helicopter's camera
  sees 2 cells further. Ground sight is unchanged. `World.AirCoverOf(u)` (the leaf marker), `World.SensorsOf(u)`,
  `Unit.MovedAt` (hashed when not 0). Tests: `ConcealmentTests`.
- **Forest fire** (2026-10-27; `content/mvp/fire.json`, the warhead `fire` block, `Sim/Systems/FireSystem.cs`; `docs/design.md`,
  "Forest fire"). On a map with terrain a forest cell lit by a blast burns 20 to 40 s (its trunk with it), spreads to its 8
  neighbours by their density (dense pine most, scrub least, open ground never; no wind), and burns out one forest level down:
  30 s of embers, then ash, its trunk a black snag. At most 150 cells burn at once on the map. A bomb lights 1 or 2 trees round
  its crater that do not spread; shells and missiles light nothing; a `fire` block with no `count` lights every forest cell in
  its radius (the incendiary's and the hypersonic missile's hook). Burning cells pop smoke screens (sight and missile locks as a
  smoke grenade's), burn infantry 10% a second (they walk out on their own), and every 2 s become walls to vehicles (the terrain
  rules' `fireCost`; infantry routes ignore fire). A cell whose tree a blast blew down loses a forest level too
  (`trees.blastThins`: sight opens). `World.Fires`, `BurntCells`, `BlastedCells`, `FireAt`, `FireOf`, `IsSnag`, `FireEvents`,
  `SmokeCloud.Fire`; `CommandKind.SandboxIgnite`; `SimRun -bench-fire N`. Tests: `FireTests`.
- **Incendiary bomb** (2026-10-27; `docs/design.md`, "Incendiary bomb"; `incendiary_bomb`, the `incendiary` warhead). One extra
  bomb on each medium jet (the multirole, the air superiority jet, the stealth multirole in both modes) behind the "incendiary"
  upgrade: no shipped lobby has it (a lobby's `upgrades`, `World.GrantUpgrade`, the sandbox's `SandboxUpgrade`), and without
  it the jets carry none. Dropped on a ground point only (attack ground; the F-35 picks it with `SetBombWeapon`): soldiers and
  light vehicles within 4 cells die, out to 7.5 cells they lose 60%; trucks 70% / 42%, armour 40% / 24% (shares of max HP);
  the forest within 6 cells catches, spreading at 3x, and the open ground there burns 12 s (it hurts infantry, walls
  vehicles off, leaves scorched ground), under the 150-cell cap. `Player.Upgrades`, `Unit.Upgrades`, `Unit.Carries`,
  `World.IsGroundFire`; `SimRun -bench-incendiary N`. Tests: `IncendiaryTests`.
- **Massive bomb plane** (an MC-130 type with one MOAB type bomb, 2026-10-30, branch `moab`, job MBe; `docs/design.md`, "Massive
  bomb plane", decisions 744 to 758; `mc130`, `moab_bomb`, the `moab` warhead; `Sim/Systems/RingBlastSystem.cs`; `MoabTests`). The
  Sentinel's slow (11 cells/s), long-range (40 min), unstealthy XL plane from the main air base's hangar (a tier 2 pick, built as an
  ordinary unit for now; no limit), 300 m up, the airlifter air class, 30 flares. On an attack-ground order it flies a straight
  run, releases over the point and the bomb falls straight down (delivery `straight`: no forward throw), 7.8 s from 300 m; every
  enemy player is warned from the release (delivery `warn`: `World.IncomingStrikes`, `IncomingEvents` Warned and Struck). Its
  blast (weapon `blast`, generic: `rings`, `tunnels`, `flatten`, `flattenTo`): a core to 15 cells that destroys everything on the
  ground whatever its armour with whoever is inside, a heavy ring to 40 cells (1,200), a light ring to 75 cells (300 fading to 0);
  a building it brings down takes its garrison; every tunnel within 40 cells collapses with everyone inside; the forest within 40
  cells is knocked flat to scrub (`World.Flattens`, `TerrainMap.Flattened`); the core and some of the heavy ring catch fire under
  the 150-cell cap. Shot down before the release, nothing falls.
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
- **Multi-target ground attack** (`MultiTarget` command, 2026-10-30, branch `multi-target`; `docs/design.md`, "Multi-target
  ground attack", decisions 718 to 735). The command's `aims` (up to 64 ground points and enemy units, in the order placed)
  become one objective of the whole selection (`World.MultiTargets`, `World.MultiTargetOf`; hashed only once one was made).
  Every point gets its own shot: one munition, a ripple launcher's whole salvo (weapon field `volleyPerTarget`, default true
  for a salvo; false splits a salvo round by round over the points in reach), a bomber's fire-mode group (a spread mode
  such as carpet20 becomes its first salvo mode while on the objective). Points with the fewest shots and units first (then
  the one the fewest units can take), the nearest unit first, ties by ID; the spare shots spread evenly until the units run
  dry. Each unit moves and fires through its own Attack-ground path (attack runs, set-up, bomber passes; a jet flies on to
  its next point after its break). A dead, dry or re-ordered unit leaves and its point goes to the next free unit. A point
  counts as hit when its shot is released. `World.PreviewMultiTarget` is the starting assignment for the client's lines.
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
  (archetype, top-left cell, `facing`: 0, 8, 16 or 24, turning the layout clockwise, East and West
  swapping a non-square footprint; a factory stands as laid out, refused at East and West if non-square) places a site
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
    parked at a station of a main air base, sends the vehicles to its ramp point (`World.AirliftRamp`), or the nearest cell
    they can stand on when the ramp point's cell is off-limits to them, as in a heavy bay (`World.AirliftBoardSpot`: the board
    range counts from it); one at a time they go up it (`Unit.AirliftBoard`) and are aboard (`Unit.Inside`). The first takes its containers off: while a seat is in use
    (`Unit.Lift`) it loads none and refuses Airdrop. `Deliver` (39) to a forward airbase: at its stand they drive off 5 s
    apart to the field garage's rally point; `Exit` on it parked lets them out; destroyed, they are lost with it. With them
    aboard it never leaves on its own: a Rearm order services it and it stays parked, held, until an order. Queries
    `World.AirliftEvents` (`Boarding`, `Aboard`, `PalletsOff`, `DroveOff`, `Lost`), `AirliftAboard`, `AirliftOf`,
    `AirliftDocked`, `CanAirlift`. Hashing: `AirliftBoard` and `Lift` only when not 0.
- **Howitzer** (`howitzer`, M777 type, 2026-10-24; `howitzer_155`; `Sim/Systems/HowitzerSystem.cs`; `HowitzerTests`; design:
  [docs/design.md](docs/design.md), "Howitzer"). The Resistance's first structure that fires: a 10 x 10 emplacement a worker
  builds (40 s, 1,500), 1,200 HP of building armour, 4 cells of sight past its footprint, tags `structure` and `artillery`.
  - **Crew** (a `crew` block on a structure: one seat, no passengers): any soldier of its side takes the seat (`Enter`, from
    1.5 cells of the footprint, once it is finished); without him it never fires and takes no orders. `Exit` puts him out
    past the middle of its south edge. `crew.occupantDamage` / `crew.occupantWarheads` (15%, the explosives): he takes that
    share of an explosive hit on the gun and can die in the seat (it falls silent, standing); destroyed, he bails out by
    `crew.survival` / `crew.exitDamage` (50%, 40%).
  - **Gun:** 15 shells of 300 (`heavy_rocket` warhead: 600 to buildings), a 3.5-cell splash (100% to 10%), a small runway
    crater, 15 to 80 cells counted from its centre (every structure's weapon fires from its centre), one every 8 s, a slow
    arc (apex 50%, 10 cells/s), scatter 1 to 3 cells by distance, `interceptible: "rocket"`. Its turret traverses all
    round, slowly (archetype `traverseTicks` 8: a facing every 8 ticks, half a turn in 6.4 s; a slow gun stays laid).
  - **Targeting:** on its own only at enemies its side spots (spotters), preferring a group (weapon `groupRadius` 3.5
    cells, `groupStructure` 3: the candidate with the most enemies round it first, after the attacker and threat rules);
    attack ground at any point between 15 and 80 cells, seen or not (a point outside is refused); `Queue` (Shift) adds
    points to `Unit.GroundQueue` (any ground unit on an attack-ground order, up to 16): a salvo at each in turn.
  - **Reload** (weapon `truckReload: { cost, everyTicks, range }`; 20, 2 s, 15 cells; the only way it reloads: no depot,
    no service): a cargo truck of its side sent to it (`Unload`, 12) or standing still and idle within 15 cells of its
    footprint hands it a shell every 2 s for 20 supply until it is full or the truck has less than 20; the truck keeps the
    rest (`Unit.FeedTo`, `Unit.FeedLeft`), unloads it at a depot or serves another gun. One truck a gun at a time.
  - **Flash** (weapon `reveal` 200): every shot shows it to every enemy for 10 s (`Unit.RevealUntil`): spotted through
    fog and smoke, a target as if seen, started again by each shot.
  - **Queries:** `World.EmplacementOf(unit)` (rounds, max, crew, reveal left, the truck feeding it and its next round,
    reload left), `World.EmplacementEvents` (`Fired`, `Revealed`, `RoundIn`, `ReloadEnded`, `CrewIn`, `CrewOut`,
    `CrewBailed`, `CrewLost`), `World.RevealLeft`, `World.RevealedBy(unit, player)`, `World.FeedingOf(truck)`,
    `World.CanFeed(truck, gun)`, `World.ShotAt` (the range rings), `Unit.Cargo` (a truck's supply left).
  - **Hashing:** `FeedTo`, `FeedLeft`, `RevealUntil` and `GroundQueue` only while one is set: games without them hash as before.
- **Sentinel cargo truck** (`sentinel_cargo_truck`, an 8x8 hook-lift truck, 260 HP, speed 22): the `cargo` effect of
  the cargo truck (400 supply, unloads into a friendly depot all at once in 3 s); it replaces `cargo_truck` in the
  Sentinel's roster and lobby (the Resistance keeps it). Built in either garage; refilled only by garage service.
- **Garrisonable buildings** (`garrison` on a structure archetype:
  `block_panel_5`, 23x6 cells, holds 10; `block_brick_4`, 14x7, holds 6; the town
  buildings `house_small` 5x5, holds 4, 700 HP; `villa_walled` 8x9, holds 6, 1000 HP;
  `shop_row` 7x6, holds 6, 1100 HP; `skeleton_frame` 8x6, holds 8, 1300 HP; the countryside
  homes `farmhouse_stone` 7x5, holds 5, 900 HP; `house_unfinished` 6x5, holds 5, 800 HP;
  `house_courtyard` 9x9 (the walled yard included), holds 5, 800 HP; `villa_tiled` 7x6, holds 6,
  900 HP; `shack_tin` 4x4, holds 2, 350 HP; and the second batch `shepherd_hut` 4x4, holds 2,
  400 HP; `mountain_house` 6x6, holds 5, 900 HP; `mudbrick_house` 6x5, holds 4, 600 HP; the stone village
  houses `stone_rowhouse` 4x6 (built to stand wall to wall), holds 4, 900 HP; `stone_house_arch` 6x6,
  holds 5, 1000 HP; `stone_tower_house` 5x5, holds 5, 1300 HP; the farm
  shed `farm_shed` 8x5, holds 4, 600 HP; the long barn `farm_barn_long` 15x4, holds 6, 900 HP; the town filler `house_over_shop` 6x5, holds 6, 1000 HP; the
  third batch of town buildings `corner_building` 7x7, holds 6, 1100 HP; `workshop` 7x6, holds 4,
  800 HP; `apartment_3f` 10x6, holds 8, 1400 HP; `apartment_l` 12x10 (the inner yard included, the
  door is the yard gate), holds 10, 1800 HP; `office_block` 8x8, holds 8, 1600 HP; the fourth batch
  `school` 12x10 (the walled yard included, the door is the yard gate), holds 10, 1600 HP; `clinic`
  8x7, holds 6, 1200 HP; `warehouse` 12x8, holds 8, 1400 HP; `fuel_station` 10x8 (the forecourt
  included), holds 3, 700 HP; `factory` 14x10 (the yard included, the door is the yard gate), holds
  10, 2000 HP; and the worker-built `observation_post`, 5x4, holds 3, 1500 HP: `owned` (its builder's for good, only
  its side enters, never neutral; placed like any structure, not neutral), occupants out of its
  ruin at min(HP, 40%) for sure, `launcherTags` [sniper] (its abilities, the scout drone launch
  `scout_station_launch`, work only while a sniper is inside: `World.PostLauncher`), and a `highGround` vision bonus on the archetype: +5% per metre
  above the mean ground within 32 cells, capped at +60% (`World.SightRange`). Nobody enters a
  building before it is finished).
  They spawn neutral (owner 0) and block their footprint for ground units;
  the rubble keeps blocking after they fall. A structure with `neutral: true` and no
  garrison (the farm buildings `greenhouse` 10x4, 150 HP; `hay_store` 6x4, 300 HP;
  `chicken_coop` 3x3, 100 HP; `farm_water_tower` 3x3, 400 HP; the town `kiosk` 2x2, 150 HP; `market_stalls` 8x6, 200 HP; `water_tower_town` 4x4, 600 HP; `cell_tower` 3x3, 400 HP) is placed the same way,
  neutral for good, and nobody enters it (`Archetype.Neutral`, `Archetype.PlacedNeutral`). Infantry (workers too) given an
  `Enter` order on one walk to its nearest door (`doors`, cells just outside
  the footprint, turning with a turned building: `World.DoorCell`) and go in while there is room; a building belongs to
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
- **Long-range rocket truck** (`longrange_rocket_truck`): `longrange_rockets`, 10 unguided
  rockets fired one every 1.5 s from 50 to 130 cells (the second-longest ground missile), 300
  damage in a 5-cell splash with 4 to 6 cells of scatter, `rocket` interception. Deploys in
  6 s, stows in 5 s, fires only on orders, and reloads only at a depot and only stowed: 20 s
  and 80 supply a rocket. `LongRangeRocketTests`; `docs/design.md`, "Long-range rocket truck".
- **Hypersonic missile truck** (`hypersonic_truck`, one a player at a time: exclusive group
  `hypersonic`): `hypersonic_missiles`, 2 missiles 4 s apart (a Shift-queued second point takes the
  second) from 150 to 1,500 cells (the longest range), at any ground point seen or not. The weapon's
  `terminal` block flies a closed form of the missile's age: one smooth arc at 125 cells/s over the
  ground, off the truck steeply, peaking a fifth of the way at a tenth of the range (100 to 250 m:
  `peak`, `peakMin`, `peakMax`, `peakAt`), down to a separation point 30 cells out and 72 m up
  (`from`, `slope`), then a curving terminal dive at 210 cells/s (the fastest projectile) that
  weaves up to 1.5 cells, seeded per shot (1,500 cells in 11.9 s). 1,400 damage on the `ballistic` warhead,
  full within 4 cells and 30% out to 10, up to 1 cell of scatter; the weapon's own `fire` block lights
  the forest inside 4 cells, spreading. Only the Patriot and THAAD types engage it (class
  `hypersonic`, about 5% and 13% a fuse), never in its dive, and only when its point is within 15
  cells of their side's units (`defendRadius`). Every enemy is warned 5 s before the
  impact (`World.IncomingStrikes`, `IncomingEvents`); each launch shows the truck to every enemy for
  20 s (`reveal`). Depot reload stowed: 90 s and 400 supply a missile. Renderers draw the flight with
  `World.FlightPointAt(p, tick, permille)`. `HypersonicTests`; `docs/design.md`, "Hypersonic missile truck".
- **Cruise missile truck** (`cruise_truck`, 2026-10-28; `Sim/Systems/CruiseSystem.cs`; `CruiseTests`): `cruise_missile`, 4
  missiles one every 2 s, each on its own route: `AttackGround` with `Command.Route` (up to 8 waypoints, then `Cell`; Shift
  queues the next missile's route; 150 to 2,000 cells of straight legs, else refused). The weapon's `cruise` block plans the
  whole flight at launch: 40 m straight up in 2 s, then 15 m over the highest ground and treetops 10 cells ahead at 35 cells/s,
  climbing at most 20 m/s and descending at most 15 m/s, a 6-cell arc at each waypoint, a pop-up to 40 m and a dive over the
  last 4 cells; a leg it cannot clear is refused (`World.CheckCruiseRoute` names it). Its `lowFlight` block: below 30 m a
  sensor that is not an aircraft sees it only within 120 cells with a clear line over the terrain and treetops (`ClearSight`);
  signature 40 / 150 / 100 (weapon `signature`). Unseen it warns nobody; seen, that player is warned while seen and 5 s after
  (`IncomingStrikes`, `IncomingEvents` `Seen`). Air class `cruise`: the Patriot type best 85, the air defence vehicle and the
  Igla as a drone, THAAD not below 30 m, no fighter. 900 on `heavy_missile`, full within 3 cells, 30% to 8, no fire. Each
  launch shows the truck for 20 s; depot reload stowed, 60 s and 300 a missile. It sets up only where no cell under it is
  steeper than 15% (archetype `deploySlope`; Deploy refused with `Slope`; `World.FootprintGrade`, `DeploySlopeOk`). Renderers:
  `World.FlightPointAt`, `Projectile.Cruise`, `CruiseWarned`. `docs/design.md`, "Cruise missile truck".
- **Jericho type ballistic missile truck** (`jericho_truck`, Sentinel, 2026-10-30; a tier 2 pick built as an ordinary unit
  for now; `JerichoTests`): one `jericho_missile`, 300 to 2,500 cells (the longest straight shot) at any ground point. Slow:
  speed 10, sets up in 20 s. The weapon's `terminal` block with `boostTicks` (a vertical start: straight up off the erector,
  pitching over onto the arc with a continuous velocity), a very high arc at 119 cells/s over the ground (peak half the range,
  600 m to 2.5 km) and a steep fall (about 81 degrees at the end): 2,500 cells in 25.05 s. 2,500 damage on `heavy_ballistic`
  (6,250 to any building: every building falls), full within 8 cells and 30% out to 16; the forest inside 8 cells burns.
  Class `ballistic` (not the S-400 type's). A 10 s warning to every enemy; each launch shows the truck for 30 s. Reload only
  at a supply depot or a field ammo point, packed up, 180 s and 1,000; a garage's service never reloads it
  (`depotReload.noService`). `docs/design.md`, "Jericho type ballistic missile truck".
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
- **S-400 type battery** (2026-10-27; `Sim/Systems/LongRangeRadarSystem.cs`; `S400Tests`): the Resistance's `s400_radar`
  (8x8 truck, 320 HP, 2.5 cells/s, 3,000, 60 s at the vehicle garage) and `s400_launcher` (340 HP, 2,500, 55 s), both set up
  in 30 s and packed up in 20 s. The radar searches 800 cells of slant from 25 m up (sweep 4 s, `cueLock` 70), with
  `counterStealth` 50 (the stealth bomber seen out to about 500 cells, not 200), a `radarHorizon` (beyond 200 cells, below
  50 m, ground-masked) and an `emitter` (enemy radars within 800 cells see it while it searches: a 2-cell disc, 10 s after
  each sweep; Hide switches it off). Launchers within 100 cells of a set-up radar fire `s400_missile`: 4, one every 3 s, 20 to
  600 cells, 100 cells/s, never below 20 m, aircraft only, semi-active (lost when the radar dies or packs up), 2 at a bomber;
  each launch reveals the launcher for 20 s; depot reload packed up, 30 s and 400 a missile. For renderers: `World.RadarOf`
  (`CounterStealth`, `Horizon`, `EmitterRange`), `RadarSearching`, `RadarSweep`, `RadarTracks`, `SignatureSeenBy`,
  `BelowRadarHorizon`, `CounterStealthOf`, `RadarSpots` / `RadarSpotOf` (the reveal), `BatteryRadar`, `BatteryLaunchers`,
  `BatteryLost`, `RevealedBy`. See `docs/design.md`, "S-400 type air defence".
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
- **Launchers aim with the hull** (2026-10-28; `HullAimTests`; `docs/design.md`, "Launchers aim with the hull", decisions
  621 to 632). A truck launcher's box is fixed on the hull and only elevates (archetype `hullAims`, no `turret`): stowed, it
  turns in place to face its target before it sets up; set up it never turns, and a target off its arc (`hullArc`, degrees
  either side, default one facing of 11.25 degrees) between salvos makes it pack up, turn and set up again. `hullArc` 180 for
  missiles that turn after the rail (the drone and cruise trucks). `verticalLaunch` (the S-400 type launcher, the hypersonic
  truck) never turns. Hull-aiming: `heavy_rocket_launcher`, `heavy_rocket_truck`, `longrange_rocket_truck`, `rocket_jeep`,
  `missile_truck`, `ballistic_truck`, `kaman_truck`, `drone_truck`, `cruise_truck`; the air defence vehicle (`sam_vehicle`)
  has no turret now (its hull turns at the old turret's rate). Unchanged: the Patriot and THAAD types, the light and medium
  rocket trucks, the SA-6 type. For all of them `TurretFacing` equals `Facing`.
- **Supply.** Only supply depots rearm: they refill salvos and drone and
  trap charges in their radius, and units in a friendly depot's radius
  reload 20% faster. A depot holds a finite stock (`supply.stock`, 1000,
  starting full) that never refills by itself: each salvo or charge it
  gives costs 1, and launchers it reloads round by round pay their
  weapon's `depotReload.cost` per rocket (rocket truck 10, heavy rocket
  truck 40). When the stock is below a round's cost the launcher waits at
  the depot (`Unit.DepotStarved`) with its reload count reset.
- **Cargo trucks** (`cargo_truck`, `cargo` effect) refill depots; trucks
  no longer rearm units themselves, except the howitzer's shells (below). A cargo truck holds 0 to 400 supply (full
  as it spawns; a howitzer takes part of a load). An `Unload` command onto its side's depot
  drives it into the depot's radius; a truck with supply aboard standing still in a
  friendly depot's radius (sent there or not, not handing a howitzer shells) unloads: 60 ticks (3 s)
  standing there, then the whole load goes into the stock at once. It
  starts only when the depot has room for all of it (counting trucks
  already unloading there) and otherwise waits beside it, so nothing is
  wasted. Moving off, leaving the radius or losing the depot cancels the
  unload and the truck keeps its load; `LoadCargo` tops a truck up to 400; an empty truck does nothing (supply
  routes come later).
- **Stances** (`SetStance` command, `stance` field; `Unit.Stance`, hashed when not Guard; the
  archetype's `stance` sets the start): Guard (the default: engage spotted enemies in reach, and
  retaliate on the leash), Aggressive (also hunt what is spotted within vision x 1.33), Hold ground
  (engage in reach, never move: Guard before the unit AI), Return fire (only the enemy that last
  hurt it, `Unit.LastHitBy`), Hold fire (never fire on its own; orders are obeyed) and Hide
  (2026-10-25, concealment from the air: exactly as Hold fire, so a unit under trees gives itself away only when ordered).
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
- **Infantry squads, phase S1** (2026-10-28; `content/mvp/squad.json`, `Sim/Squad.cs`, `Sim/Systems/SquadSystem.cs`;
  `docs/design.md`, "Infantry squads"; `SquadTests`). 2 to 10 infantry of one player (no workers or pilots) ordered as one:
  `CommandKind.FormSquad` (45: form squads of 10 in ID order, add loose soldiers up to 10, merge squads of 10 or fewer
  together), `Detach` (46: loose with the squad's stance), `Disband` (47: everyone loose in place), `SetSquadMode` (48,
  `Command.Index`: `SquadMode` March or Assault). The leader is the lowest-ID member who is not a medic, sniper or mortar; a
  member who dies leaves the same tick, the next leads, at one member the squad disbands. One path a squad: its anchor (the
  leader's slot) walks it at the squad's speed and each member steers to his slot round it. **March**: a file 1.5 cells apart
  along the anchor's trail at the slowest member's speed (recon ahead, the flanks behind the leader, the medic in the middle,
  the long flank, air guards and support at the rear). **Assault**: a line 2 cells apart across the move at 80% (the flanks at
  the ends; medic, recon, long flank, air guards and support behind). Roles come from class tags (the `squad` block's `roles`).
  That role order is the shape at rest; on a new order the members swap slots by where they stand along the move (the foremost
  who may lead takes the head), a member whose slot is behind him waits for it instead of walking back, and the squad never
  waits for him (`docs/design.md`, "Squads: slots by place"; `SquadOrderTests`).
  Move, Attack-move and Set stance given to a member are the squad's; Attack, Attack ground, Guard, Scatter, Enter, Exit and
  Stop go to every member, the squad re-forming round its leader once they are done; it boards and comes out as one. In a fight
  each member fights as the unit AI says, his slot his leash point; on Attack-move the squad waits while one fights. Queries:
  `World.Squads`, `Squad`, `SquadOf`, `Unit.Squad`, `SquadSlotPoint`, `SquadSlotCell`, `SquadRoleOf`, `CanJoinSquad`,
  `SquadEvents`. Hashed once a squad exists. `SimRun -bench-squads 50`: the squad pass 0.09 ms of tick p99.
- **Infantry squads, phase S2: Hold** (2026-10-28; the `hold` part of `content/mvp/squad.json`,
  `Sim/Systems/SquadHoldSystem.cs`; `docs/design.md`, "Squads: Hold and position picking"; `SquadHoldTests`). `SetSquadMode`
  with `Index` 2 (`SquadMode.Hold`), `Cell` the spot and `Cell2` the dragged facing's end (the threat, 4 to 30 cells off; none:
  10 cells ahead the way the squad walked). Once per order (a squad farther than 12 cells marches there first) it scores every
  free cell within 12 cells of the spot (at most 600) and the garrisonable buildings near it with integer factors (cover, trees,
  sight with the fog's line of sight, open ground and roads, spacing, safety, range, nearness), weighted per role in the
  content; members pick in the order recon, long flank, flank, line, air guard, support, medic, each the best cell left (ties
  to the lowest cell index), within their roles' limits (the medic 2 cells and the mortar 10 behind the line, Iglas 8 cells
  apart). Riflemen, RPGs and the sniper take buildings in reach of the threat; the medic, the Kornet team, the mortar and the
  Iglas stay out. The squad stays until a new order; the mortar sets up; in a fight each member's leash point is his Hold cell.
  Queries: `Squad.HoldSpot`, `HoldFacing`, `HoldThreat`, `HoldPlaced`, `HoldPicks`; `World.SquadHoldPick`,
  `SquadHoldGarrisoned`, `SquadHoldScores` (a debug view per candidate); `SquadEventKind.Placed`. `SimRun -bench-hold 50`: the
  squad pass 0.60 ms of tick p99, one order's scoring 0.23 ms.
- **Infantry squads, phase S3: Ambush** (2026-10-28; the `ambush` part of `content/mvp/squad.json`,
  `Sim/Systems/SquadAmbushSystem.cs`; `docs/design.md`, "Squads: Ambush"; `SquadAmbushTests`). `SetSquadMode` with `Index` 3
  (`SquadMode.Ambush`), `Cell` the kill zone's start (where the enemy comes in) and `Cell2` its far end; 6 to 30 cells, else refused
  (`OrderRefusalReason.ZoneLength`). Both sides of the zone are scored with Hold's machinery (the zone as the threat, plus near
  the role's end, the lane down the road, the approach seen, hidden from the zone) and the better is taken; everyone stays on it:
  riflemen 3 to 8 cells off the zone, RPGs at its two ends, the Kornet toward the far end, the sniper back toward the approach,
  the medic hidden behind the line, the mortar set up 8+ cells back facing the zone; garrisons on the side for riflemen, RPGs and
  the sniper. The line lays mines from its own stock: anti-tank on the road from the far end back, anti-personnel off the far
  side, the engineer one booby trap. Everyone holds fire; Setting, then Ready. It springs on its own on the first of: an enemy
  vehicle at the far end, 3 enemies in the zone, a mine in it going off, an enemy spotting a member, a member hurt, or Fire
  (`CommandKind.SpringAmbush` 49). Sprung, everyone takes the squad's stance and fights from his spot (no chasing; RPGs and the
  Kornet vehicles first, the line infantry first); 10 s calm and it is Ready again; mines are not laid again. A move ends it.
  Queries: `Squad.AmbushStart`, `AmbushEnd`, `AmbushSide`, `AmbushSpot`, `State` (Setting, Ready, Sprung), `AmbushTrigger`,
  `AmbushTasks`, `AmbushMines`; `World.SquadAmbushMines`; `SquadEventKind` Setting, Ready, Sprung, MineLaid. `SimRun
  -bench-ambush 50`: the squad pass 0.48 ms of tick p99.
- **Infantry squads, phase S4: air ambush** (2026-10-28; the `air` part of the ambush in `content/mvp/squad.json`,
  `Sim/Systems/SquadAirAmbushSystem.cs`; `docs/design.md`, "Squads: Air ambush"; `SquadAirAmbushTests`). The same Ambush order
  given to a squad more than half Iglas sets an air ambush (`Squad.AmbushAir`; `World.AirAmbushSquad` says which kind an order
  would set): `Cell` to `Cell2` is the air corridor, 20 to 120 cells, else `ZoneLength`. The Iglas spread along it, slot points 8
  to 15 cells apart, each under trees near his slot and never closer than 8 cells to any Igla (other squads' too); everyone else
  guards them 2 to 5 cells off, toward the likely ground approach (10 cells before the corridor's start). No mines. Everyone holds
  fire, hidden under the trees. It springs when an enemy aircraft is within reach of 2 Iglas (`AmbushTrigger.Air`), a member is
  hurt or on Fire; sprung, the Iglas lock on one target (`Squad.VolleyTarget`) and launch together in one tick
  (`SquadEventKind.Volley`), so the jet's break and flares face several missiles at once: over 12 seeds a volley's missiles kept
  65.6% of their kill chance through the flares against 30.0% for single launches, and brought the jet down in 6 runs of 12
  against 0. `SimRun -bench-air-ambush 50`: the squad pass 0.33 ms of tick p99, gate 0.61 ms.
- **Skirmish AI** (`Bots/SkirmishAi.cs`, `SkirmishScouting.cs`): keeps ID-sorted memories of visible enemy
  structures and forgets one only when its old cell is seen empty. Normal timed waves first target visible
  enemies, then remembered structures; with nothing known they explore reachable region probes. Idle wave
  members reserve separate probes, prioritizing unseen/oldest, then the far half, distance from home and
  region index. Passive structures get explicit attacks once visible and in weapon range. Defaults:
  `RegionSizeCells = 16`, `RescoutEveryTicks = 1200`; wave size and timing are unchanged. The AI reads only its
  own side's fog/explored state. Sentinel Watcher/Overseer missions remain a follow-up.
  Since phase S5 (`Bots/SkirmishSquads.cs`; `docs/design.md`, "Squads: the skirmish AI") it forms its infantry at home into
  mixed squads of up to 8, sets an air ambush with an Igla-heavy squad, a road ambush toward its base and a hold of the key spot
  (all inside its territory, 30 cells round its structures; at most half its ground squads at home), and sends the rest out with
  the waves in March, turning to Assault near the enemy; with nothing else left it sends everything. No random draws.
  `UseSquads = false` plays as before. `SkirmishSquadTests`; `SimRun -skirmish 24000`.
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
  footprint matters). Factories (doors, bays and exits laid out south) and tunnel portals can't
  turn this way. `FootprintRotationTests`.
- **Turned buildings** (2026-10-25; `docs/design.md`, "Turned buildings"). Every town building, home,
  farm building, stone village house, garrisonable block and prop, and the observation post, stands at
  any of four quarter turns of its layout (`Unit.Quarter`: 0 as laid out, the front to the south; 1 west;
  2 north; 3 east; hashed when not 0): its footprint swaps at an odd turn and its doors turn with it
  (`World.DoorCell`, `World.LayoutCell`, `World.FrontFacing`). Placed by `World.Spawn(def, cell, facing,
  quarter)`, by `SandboxSpawn` with `Command.Facing` 0, 8, 16 or 24, or built at that Build facing (the
  post). `World.Spawn(def, cell, facing)` keeps a structure as laid out whatever its facing, as before.
  `BuildingRotationTests`.
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
- **Stealth multirole** (F-35 type, 2026-10-24; `Sim/Systems/LoadoutSystem.cs`, `RadarSpotSystem.cs`; `StealthMultiroleTests`):
  `stealth_multirole` (class M, built only by the main air base's hangar, no cap; the multirole's airframe, the `fighter` air
  class) with two loadout modes (archetype `loadouts`; `Unit.Loadout`, `Unit.StockMax`, `Unit.Signature`): `stealth` (built in
  it: 2 heavy bombs, 10 radar missiles, 1.3x the multirole's speeds: cruise 26 cells/s, the fastest jet; signature 35 / 250 /
  65) and `heavy` (10 bombs, 2 missiles, the multirole's speeds; 80 / 130 / 90). `CommandKind.SetLoadout` (42, `Index` the
  mode) changes it only parked at a station that rearms (main air base or forward airfield), in 200 ticks, ending with the new
  mode's full load (`World.LoadoutOf`). Heavy bombs `f35_bombs` (the 2,000 lb x 1.3: 1,690, 4 / 10 cells, tier 6; `single` /
  `pair`); 8 flares. Radar spotting (archetype `radarSpotting`): flying inside an enemy air radar's coverage (tag `air_radar`:
  the mobile radar, the air defence vehicle), detected or not, reveals the radar and 2 cells round it to its side until 200
  ticks after it left (`World.RadarSpots`, `RadarSpotOf`). See `docs/design.md`, "Stealth multirole (F-35 type)".
- **Logistics** (2026-10-22; `Sim/Systems/AirdropSystem.cs`, `LogisticsSystem.cs`, `SupplySystem.cs`; `LogisticsTests`): the
  `airlifter` (C-5 type, class XL, unarmed) with 4 containers of 2,500 supply (`cargo_pallets`, weapon `airdrop`), loaded free at
  the main air base; `CommandKind.Airdrop` (38: a low pass over a drop zone, fire modes `all4` / `single`, pallets scattered,
  lost outside the zone) and `Deliver` (39: it lands at a forward airbase and unloads into its stock); the `facp` (field ammo point:
  a stock that rearms its side's ground units within 12 cells), the `supply_stash` (storage), and the `forward_airbase` (a compact
  core-founded field: 7-segment runway, 2 stands for any class, a 2-bay field garage, no production, a limited stock every
  service there draws on, at least 120 cells from the main air base: content `awayFrom`). Cargo trucks `LoadCargo` (40) at a
  store whose supply is a `cargoSource`. A store's stock never passes its cap: a pallet's excess is lost, a truck keeps what
  doesn't fit. New content fields: supply `startStock`, `storage`, `cargoSource`, `groundOnly`,
  `roundCost`, `repairCost`, `fuelCost`; archetype `dropZone`, `awayFrom`; weapon `airdrop`; production `bays` 0 (doors for a
  service block only); airfield `rows.extra`; a core type's radar is optional. Queries: `World.LogisticsEvents`, `StockOf`,
  `DropZoneOf`, `CargoAboard`, `AirfieldStore`. See `docs/design.md`, "Logistics".
- **Helicopters** (2026-10-21 night; `Sim/Heli.cs`, `Sim/Systems/HeliFlightSystem.cs`, `HeliCombatSystem.cs`,
  `HeliPadSystem.cs`, `HeliCommandSystem.cs`; `HelicopterTests`; design: `docs/design.md`, "Helicopters"): the
  Sentinel's `attack_helicopter` (AH-64 type: chin cannon, 10 anti-tank missiles, 2 rocket pods, a mast radar),
  `armored_helicopter` (Mi-28 type) and `light_helicopter` (AH-6 type: miniguns, 8 light rockets, 3 riders) and the
  `helipad` (12 x 12, built by a worker or the bulldozer; builds one helicopter onto itself, parks and services it). The
  rotary flight hovers, yaws on the spot, flies sideways and backwards, follows the terrain 20 to 60 m up and lands
  straight down (`Unit.Heli`). Missile attacks from cover: it hides behind a crest or woods, pops up to fire, ducks
  (`HeliAttack`); guns and rockets: runs that sidestep round the target. HB, the helicopter balance (`HeliBalanceTests`;
  design: "Helicopters", "HB"): the attack helicopter fires at its own mast radar's tracks from cover (lofted), in
  ripples at up to 4 vehicles (`HeliState.RippleTargets`, `Projectile.Lofted`, `World.OwnRadarTrack`); the
  armoured one's missiles (75% damage, 28 cells) are command-guided (it holds its line of sight to impact: `Unit.Guiding`). Brought down it goes **out of control**: its HP
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
  cell, neutral, when its footprint is free, turned by the command's facing 0, 8, 16 or 24), through the command queue like any other
  order, so sandbox sessions replay exactly. Every other scenario ignores it.
  The Godot client uses it to put units under review on the map.
- **Match result.** A player with no soldiers left, on foot or driving, is
  defeated; `World.Result` reports when the match is over and who won.
- **Terrain (data).** A map may carry a terrain (`World.AttachTerrain`,
  `World.Ground`, `Sim/TerrainMap.cs`): corner heights (`HeightAt`), per-cell
  grade, forest density (0-3), roads with a kind (highway, paved, gravel, dirt, and
  the foot kinds alley, which only the foot class enters, and footpath, which
  cheapens foot routes and blocks nobody: `RoadKindAt`, `IsFootOnly`; a second
  per-cell byte, only on a map that has one) and building pads, and one path-cost byte per cell per movement
  class (`content/terrain/rules.json`: foot, wheeled, heavy wheeled, tracked;
  slope classes Flat / Gentle / Steep / Cliff, road speed and cost per kind,
  forest costs, tree trunks: "Trees as obstacles" below). Maps load from JSON plus a 16-bit PNG heightmap
  (`Content/TerrainLoader.cs`). A map without terrain is flat and hashes as
  before; the rules are not part of `content/mvp` for that reason. Maps are 1 to
  8,192 cells a side (`TerrainMap.MaxSide`, 16.4 km; any aspect, such as the
  6,000 x 3,000 of a 12 x 6 km map), heights ±256 m. Load time, memory and tick
  times: `SimRun -terrain-bench 6000x3000 -battle 200 -fog true` (a 6,000 x 3,000
  map loads in about 0.9 s and holds 288 MB of terrain; with routing, fog and a
  battle about 0.9 GB). On a big map call `World.PrepareRouting(everySector: false)`
  in the loading screen (every sector of every class takes minutes there). See
  `docs/design.md`, "Terrain" and "Map size".
- **Water.** A terrain may carry rivers (a centre line with a width), lakes and
  the sea (polygons), each with a flat surface height (the map's `water` list;
  `TerrainMap.WaterBodies`, `Water` / `OpenWater` / `WaterIdAt` /
  `WaterSurfaceAt` / `WaterFlowAt`). Open water is `Terrain.Water` on the
  grid and costs 0 for every ground class: no swimming, nothing is placed in
  it; `HeightAt` is the surface over it (sight, shells, aircraft), the bed is
  `GroundHeightAt`. A blast in it digs no crater and its event says `Water`
  (`Detonation`, `BombImpact`, `WreckImpact`, `StrafeBurst`, `KamanEvent`,
  `HeliCrash`; `World.InWater` for any point). A deck (`SetDeck`) over a
  water cell makes it flat dry ground: the bridges lay it. Maps without water
  hash as before. See `docs/design.md`, "Water".
- **Alleys and footpaths** (map `roads[].kind` `alley` / `footpath`;
  `RoadKind.Alley`, `Footpath`; `RoadKind2Tests`; design: `docs/design.md`,
  "Alleys and footpaths"). An alley is a wall (cost and speed 0) to every
  ground class but foot, so no route or movement of a vehicle enters one; for
  foot it is a paved road with no speed change. A footpath blocks and slows
  nobody; foot routes (a group's too) prefer it at paved's cost. A vehicle road
  wins an overlapping cell. Nothing but a soldier is put on an alley
  (`World.IsAlley`: structures refused, vehicles moved to the nearest other
  cell). Maps without either hash as before.
- **Bridges** (`bridge_stone` 3x8, `bridge_concrete` 5x12, `bridge_metal`
  9x18 cells; 2,500 / 3,500 / 4,500 HP; `Sim/Systems/BridgeSystem.cs`,
  `BridgeTests`; design: `docs/design.md`, "Bridges"). The map places them
  (its `bridges` list: `{"kind": "concrete", "cell": [x, y], "dir": "ns"}`,
  `TerrainMap.BridgeSites`, put up by `World.AttachTerrain`; the sandbox too),
  never players (no `construction`). A site needs both ends on dry land and
  its road crossing water (`World.BridgeRefusal`; a bad site fails the load:
  `TerrainLoader.LoadMap(path, rules, content)`). Neutral for good, it blocks
  nothing: every footprint cell carries its road kind (paved, paved,
  highway) and its cells over water are its flat deck. Nobody targets it on
  his own; ordered attacks and blasts (in the water beside it too) hurt it.
  Stages by HP (0 above 70%, 1 above 40%, 2 down to 0 HP); at 0 HP the span
  falls (stage 3): the deck is lifted, everyone on it is lost with whoever is
  inside them, paths across it are re-planned, the ruin stays at 1 HP and is
  no target. Builders of either side repair it (Repair; one builder: the
  whole bar in 30 / 45 / 60 s); the deck comes back at 40%. For renderers:
  `World.Bridges`, `BridgeAt`, `BridgeEvents` (stage, `Collapsed` with the
  units lost, `Reopened`), `Unit.Bridge` (`Down`, `Repairing`, `DeckCells`),
  `Unit.Stage`, `Unit.Turned` (the road east-west).
- **Trees as obstacles** (`Sim/Trees.cs`, `Sim/Systems/TreeSystem.cs`,
  `TreeTrunkTests`; design: `docs/design.md`, "Trees as obstacles"). Groves
  and dense forest have standing trunks, worked out from the forest density
  (one per 2 x 2 cells at most, `trees.trunkChance` 50 / 85 % in
  `content/terrain/rules.json`; a pinned integer hash, `TreeTrunks`; a bit a
  cell, not stored in maps). Each movement class says what a trunk is to it
  (`trunks`): infantry ignore them, jeeps and light trucks steer between them
  (a trunk's cell is a wall to them, so routing's clearance decides which gaps
  they fit), tanks and heavy trucks push them over: slowed to 30% while they
  press on one, it falls after 1.5 s the way they face, its stump's cell opens
  and the trunk lies 4 cells along the fall for 30 s, a wall to every vehicle
  but the pushers (who roll over it slowly). Dense forest no longer stops the
  pushers and now stops the cars. Building on forest clears its trunks and fallen trunks
  (`World.ClearTrees`). For the client: `TerrainMap.HasTrunk`, `TrunkOffset`,
  `TrunkPoint`, `Fallen`; `World.FallenTrunks`, `Stumps`, `TreePushes`,
  `TreeEvents` (`PushStarted`, `Fell`, `Expired`, `Cleared`). Maps whose rules
  have no `trees` block have no trunks. Blasts blow trees down too
  (`TreeBlastTests`): the warheads in `trees.fell` fell every trunk within a
  percent of the weapon's splash (at least 1.5 cells), each falling away from
  the impact (`TreeFallCause.Blast`): a missile 1 or 2 trees, a shell a
  clearing, a carpet of bombs a lane. A falling trunk takes 10% of max HP from
  the infantry under it.

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
