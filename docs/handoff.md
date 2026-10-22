# Handoff: engine notes for a new session (2026-10-20)

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

## What the engine has (main after the night of 2026-10-19/20: milestones E1 to E11 of the jets plan)

- **Core:** units, weapons with two-slot loadouts, warheads with per-armour
  percentages (plus `maxHpPercent` for building damage), splash (including
  two-ring), guided missiles (homing, max flight, operator line), ballistic arcs
  that meet the terrain, salvos, deploy/stow, crews and garrisons.
- **Scale:**
  - a spatial index for vision, targeting and projectiles;
  - HPA* routing with road highways, corridor flow fields, a request queue and
    formation slots; its sector cache is built ahead on quiet ticks (2,000 cells of
    search a tick, ~0.1 ms, for the classes of units that can move) or all at once by
    `World.PrepareRouting` (a loading screen: ~40 ms a class on 256 x 256);
    the connected regions that tell a route its goal can't be reached are labelled
    per sector and united over the sector links, so a map change (a building, a
    crater, rubble) relabels only the sectors it touched: ~0.05 ms a class on
    1,500 x 1,500 instead of a ~25 ms full-map flood fill (`SimRun -spike-bench regions`);
    many cells changed in one tick (an airfield laid and its ground levelled) refresh the
    routing costs in one pass over their bounding box, not 9 x 9 cells per changed cell:
    the tick after a field is laid on 256 x 256 ~1-2 ms instead of ~40 ms warm, 11 instead
    of 226 ms cold (`SimRun -spike-bench field -prepare true`);
  - a MovementSystem with avoidance, collision, turn rates, clearance,
    slope and forest speed, arrival and stuck detection.
  - The 10,000-unit bench is at about 15–17 ms tick p99 (M3 Pro, 2026-10-20).
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
    kill roll;
  - the scout drone (an orbiting high-altitude spotter) and the observation post.
- **Air combat** (E2 to E11; `docs/design.md`, "Air combat"): one framework for every
  aircraft: missions (Orbit, Patrol, Guard, Search, Roam, Strike; `AirTask` 30) over a
  flight controller with edge avoidance, the drone law or the jets' fixed-wing energy
  envelope; the owner's rules (home on its own only to refuel, Rearm 29 rearms and repairs,
  resume the task); air classes as content, signatures and team cueing; air-to-air
  (missiles, guns, flares, chaff, breaks); air-to-ground (strike runs, an exact bomb release
  solution, guided bombs, strafing, airstrike tiers); crash and ejection (wrecks, the pilot,
  the technical's heavy MG against low aircraft); the two jets (multirole and air
  superiority) built by the airfield facility; the overhead break, the military or
  afterburner takeoff and the per-aircraft home check (a jet's corridor, a drone's join).
  E12 (2026-10-20): the height planner (a 5 s look-ahead over terrain and structure tops,
  bounded vertical acceleration: the "bouncing" fix), the fly zone past the map's edge
  (`flight.flyZoneMargin`, decisions D-FZ1 to D-FZ4) and a jet's quick turn on a fresh
  player order (D-TURN1). E13 (2026-10-20): the attack-run state machine (Ingress, Align, Run, Break, Extend,
  Reposition, Egress; archetype `attackRun`), elements of up to four with formation slots off the lead's path and
  staggered runs (`element`, `Unit.Wingman`), pursuit separation (`pursuit`, guns 15 cells), the break with flares
  (`breakPk`), heights, tanks and transit speeds by map size (`heightByMap`, `enduranceByMap`, `speedByMap`), no
  attack order dropped silently (`World.OrderRefusals`, implicit rearm of parked jets), `CommandKind.SandboxSetHp`
  (31) for scripted demos (design rows 68 to 76).
- **The airfield** (M1 to M3b): building it piece by piece, holding, landing, taxi,
  parking, service, recall, craters and repair (`docs/design.md`, "Airfield").
- **The main air base and the radar** (H1, 2026-10-21, branch `heavy-h1`; `docs/design.md`, "Main air base
  and radar (H1)", decisions 78 to 94): the airfield type `main_air_base` with a core (`airbase_core`, placed as
  one unit, 120 s) that lays the command building (the facility), the hangar (production of every air kind) with 4
  heavy bays (classes L and XL only; heavies park nowhere else), the radar, a 9-segment runway and 4 rows; rows to a
  cap of 12; the command building down stops production and heavy rearm (refuel goes on); a forward airfield near
  the apron joins its taxi graph; the radar (built in and the standalone `radar_site`) tracks air (`airVision`),
  spots ground (`groundRadar`: vehicles anywhere, infantry in the open) and clears the fog; rubble turns it off.
  The heavies themselves (A-10, AC-130, B-52 types) are H2 to H4; `MainAirBaseTests` uses fixtures.
- **The ground attack jet** (H2, 2026-10-21, branch `heavy-h2`; `docs/design.md`, "Ground attack jet (H2)",
  decisions 95 to 108): `attack_jet` (an A-10 type, class L, the `attacker` air class: 4 Igla hits), 8 `heavy_bombs`
  (tier 6, 1.5x blast, released low over the target), bomb fire modes (`CommandKind.SetFireMode` 32, `Unit.FireMode`),
  Shift-queued aims bombed together when close (`Unit.Aims`) and `CommandKind.AttackLine` (33), all in
  `Sim/Systems/AttackPlanSystem.cs` and generic for free-fall bombs (the B-52 type, H4, adds a mode); `AttackJetTests`.
- **The gunship** (H3, 2026-10-21, branch `heavy-h3`; `docs/design.md`, "Gunship (H3)", decisions 109 to 125):
  `gunship` (an AC-130 type, class XL, the `gunship` air class: 8 missile hits, 360 m up above the Igla's new 320 m
  `ceiling`, 45 min of fuel), its orbit attack on Attack-ground / Attack orders (a left-hand pylon turn of 30 cells,
  `Unit.Gunship`), three `sideFire` cannons (100 / 20 / 1) firing only on the circle at what its side sees in a
  15-cell area, scattered; home on its own when out of rounds; 12 angel-wing flares. `World.SideFires`,
  `World.RadarTracks`; the Watcher and Overseer got hangar build times. `Sim/Systems/GunshipSystem.cs`, `GunshipTests`.
  Follow-ups: the drones' hangar cost is 0; no cap on gunships yet (the owner: later, e.g. 2 a player).
- **The strategic bomber** (H4, 2026-10-21, branch `heavy-h4`; `docs/design.md`, "Strategic bomber (H4)", decisions
  126 to 140): `strategic_bomber` (a B-52 type, class XL, the `bomber` air class: 6 missile hits, 400 m up above the
  Igla ceiling, 30 min of fuel, wide turns), 20 `strategic_bombs` (tier 7, level from 400 m, about 50 cells of fall),
  fire modes `carpet20` (default, `spread`: one stick for the pass, end to end on a line) and `salvo4`; `overfly`
  (it passes over its drops before the break); no air weapons; 30 flares, two a missile. `StrategicBomberTests`.
  The fly zone margin is now 65 cells (its turn). Follow-ups: no bomber element tuning; with carpet20 far queued
  aims get nothing; no cap on heavies yet.
- **The supersonic bomber** (B-1 type, 2026-10-22, branch `b1-bomber`; `docs/design.md`, "Supersonic bomber", decisions
  146 to 157): `supersonic_bomber` (class XL, the `bomber` air class, 400 m up, cruise 18 / run 22 cells/s, 25 min of
  fuel), 24 `supersonic_bombs` (1,400, tier 6) dropped in **rows across the flight path**: a fire mode's `across` /
  `acrossSpacing` (generic; `ExpandRows` in `AttackPlanSystem.cs`, the push across in `ReleaseBomb`): `row4` (default, a
  row of 4 at each aim, 9 cells wide) and `block24` (6 rows of 4 in one pass, about 9 x 15 cells, no gaps under its 4-cell
  kill zone). `StrikeRun.Across`, `DropSide`, `DropPoint(i)`; each bomb's landing in `BombReleases`. `SupersonicBomberTests`.
  Follow-ups: row4 on a dragged line lays one row (decision 153); no cap on heavies yet.
- **The Kaman drone** (Kaman-22 type, 2026-10-22, branch `kaman`; `docs/design.md`, "Kaman drone", decisions 158 to
  172): the Resistance's `kaman_drone`, a flying unit (air class `kaman`: 2 Igla hits, 1 air defence missile hit, the
  jets' weapons) launched by the `kaman_truck`'s rail or the engineer's `kaman_station` (`kaman_station_kit`) at a unit
  (followed; flown to its last known position once its side stops seeing it) or a point, 60 to 1,200 cells; 150 m up at
  15 cells/s, 2 missiles from 30 cells, then a dive into the target (520, 6 cells, armour dies, buildings 45%). New
  weapon field `launches`, archetype block `kaman`, `Unit.Kaman`, `World.KamanEvents`; a unit with several
  depot-refilled stocks reloads one at a time (`Unit.DepotReloadStock`). `KamanDroneTests` (20). Follow-ups:
  helicopters have no air weapon to shoot it with (decision 167); no skirmish AI use.
- **The mobile radar** (SA-6 "Straight Flush" type, 2026-10-22, branch `mobile-radar`; `docs/design.md`, "Mobile radar",
  decisions 173 to 182): the Resistance's `mobile_radar` (light tracked, 280 HP, speed 30, 1,600, 40 s at the vehicle
  garage, no weapon) searches the air only set up (Deploy 8 s, Stow 6 s) out to 400 cells by signature, feeding its
  side's team cueing and fog. New generic archetype field `cueLock` (70): a ground air defence weapon of its side whose
  target a set-up cueing radar tracks locks in that percent of its lock time (AA soldier 1.5 -> 1.05 s, air defence
  vehicle 1 -> 0.7 s); never range, never kill chance; off for every other unit (the radar truck too). Client queries in
  `Sim/Systems/RadarCueSystem.cs` (`RadarSweep`, `RadarEyeRaw`, `RadarCuedShooters`, `RadarCued`, `AirLockTicks`),
  `RadarOf` (+`CueLock`), `RadarTracks` (empty while packed up). `MobileRadarTests` (21). Follow-ups: no skirmish AI use;
  no emitter (anti-radiation) rule.
- **The SA-6 launcher** (2K12 Kub type, 2026-10-22, branch `sa6`; `docs/design.md`, "SA-6 launcher", decisions 183 to
  193): the Resistance's `kub_launcher` (tracked, 280 HP, speed 30, 2,000, 45 s at the vehicle garage; Deploy 5 s, Stow 4
  s) with 3 `kub_missiles` (350 cells, 55 cells/s, about 1.3x the Osa's kill and hit numbers, capped at 95%). A separated
  battery: the new interceptor block `battery` (`linkRadius` 60 cells, `radarTags`, `semiActive`) makes it fire only at a
  target a set-up mobile radar of its side within 60 cells tracks, and its semi-active missile is lost the tick no radar
  linked to its launch point tracks the target. Depot reload packed up, one every 20 s for 200 (it no longer packs up
  while it tracks). Code `Sim/Systems/BatterySystem.cs`; client queries `BatteryRadar`, `BatteryLaunchers`, `BatteryOf`,
  `BatteryLost`. `KubLauncherTests` (29). Follow-ups: no skirmish AI use; no transloader; it does not set up again after a
  depot reload.
- **Fog of war, traps and tunnels:** per-team line of sight over terrain and
  woods, mines with detection, and tunnel networks with two-stage destruction.
- **Unit AI:** five stances, retaliation with a leash, attack-move, Guard
  orders, target priorities, scatter and crushing; vision and target search are
  staggered (every 4 ticks, phased by ID).
- **Two factions:** the Resistance (`battle.json`, the golden replay's mirror)
  and the **Sentinel** (`content/setups/sentinel.json`): the shared basics plus
  the Watcher strike drone, the Overseer surveillance drone (`OverseerTests`), the airfield
  and the two jets; the drones are each launched by its own
  control station (`watcher_station`, `overseer_station`: one drone type per station; `Sim/Systems/WatcherSystem.cs`, `WatcherTests`; `docs/design.md`, "Sentinel
  faction"). A faction is content; the only Sentinel-specific behavior is the
  watcher system, inert unless content uses it.
- **Military garage (2026-10-21):** the Sentinel's `military_garage` shares its 5 doors between
  production (up to 3) and vehicle service (`CommandKind.Service`: reload 5 s, repair 10 s, cargo
  refill 10 s), and its `sentinel_cargo_truck` replaces the cargo truck (`ServiceSystem.cs`,
  `MilitaryGarageTests`; README "Military garage"; `docs/design.md`, "Sentinel faction").
- **Assault medic (2026-10-21):** the Sentinel's `sentinel_medic` (75 HP, rifle, 260) and the `aura` effect kind run
  by `AuraSystem.cs`: 3 HP every 1.5 s to friendly `infantry` within 6 cells, at most one heal per interval however
  many medics (the strongest), not himself, nobody inside anything; two in the Sentinel lobby (`MedicTests`; README
  "Assault medic"; `docs/design.md`, "Assault medic").
- **Helicopters (2026-10-21 night, branch `helicopters`; `docs/design.md`, "Helicopters", decisions H-1 to H-22):**
  the Sentinel's `attack_helicopter` (AH-64 type, a mast radar), `armored_helicopter` (Mi-28 type) and
  `light_helicopter` (AH-6 type, 3 riders), and the `helipad` (builds one helicopter onto itself, parks and services it;
  the main air base's hangar builds them onto its stands too). A new flight kind, `rotary` (`RotarySpec`, `Unit.Heli`):
  hover, yaw on the spot, sideways and backward flight, terrain following 20 to 60 m up, vertical landing anywhere.
  The flying AI hides behind a crest or woods and pops up to fire its missiles (fire and forget), runs with guns and
  rockets. Brought down by the interception model's hits (Igla 4 / 5 / 2, air defence missile 3 / 4 / 1; the Patriot
  type destroys outright) it goes out of control: HP draining over 30 s while it flies itself to the nearest pad (a hard
  landing), else a crash landing with survival rolls; a further hit destroys it in the air. Orders as the jets' plus
  Attack-move, Guard, Load and Unload (`CommandKind` 35, 36). Queries `HeliStatusOf`, `PadStatusOf`, `RidersOf`,
  `HeliCrashes`. Code `Sim/Heli.cs`, `Sim/Systems/Heli*.cs`; tests `HelicopterTests` (29); bench `SimRun -bench-heli N`
  (100 helicopters: +1.6 ms tick p99).
- **Tools:** `SimRun` with `-bench`, `-bench-air`, `-bench-heli`, `-move-bench`, `-routing-bench` and
  `-terrain-bench`, plus headless match runs. Tests: 1028 (after E12), all green on CI.
  Tick p99: about 2.1 ms at 500 units, 15 to 17 ms at 10,000 (M3 Pro); 100 jets in a
  dogfight about 1.2 ms.

## Working practices

- **Branches and history:** work on a branch or worktree when another agent
  shares the checkout. Use fast-forward pushes and never force-push. No
  long-lived branch per faction or feature: factions are content on `main`
  (the `sentinel` branch is merged; the game pins only `main` commits).
- **Merges and the golden replay:** never hand-merge
  `tests/SimCore.Tests/testdata/battle_seed42.*`; take `main`'s, finish the
  merge, re-record once, and say why in a `Golden:` line of the commit message
  (`Golden: content hash only` when `BotMatchMatchesGoldenCommands` still passes).
- **Commands:** append new `CommandKind` values only (`CommandKindValuesAreStableAndUnique`
  pins them).
- **Tests:** add tests with every system: determinism (run twice, reverse
  order), the rules, and edge cases.
- **Benchmarks:** keep the benchmarks runnable headless; graphical stress
  tests belong to the client.

## State after the night of 2026-10-19/20, and what is left

- **Landed on `main`** (each with its tests, CI green, the perf gate passed; the golden replay
  re-recorded for the content hash only where content changed): E1 (one drone type per
  station), E2 (the owner's flight rules), E3 (missions, edges), E4 (fixed-wing flight), E5
  (air classes, signatures, cueing), E8 (the jets as content), E6 (air-to-air), E7
  (air-to-ground), E10 (crash and ejection), E9 (jets at the airfield), E11 (cleanup: the
  radar's fog spotting by slant distance, the drones' straight-in join checked at home
  choice, the bingo-release test; these docs).
- **Decisions to confirm:** every call the night took without the owner is in one table,
  `docs/design.md`, "Air combat", "Decisions to confirm", each with its commit. The open one
  with the most effect on play: the air defence vehicle's drone-era reach against jets
  (radar 260 cells, missile 220: nearly a whole 256-cell map), with the proposed levers.
- **Left for the engine (helicopters):** the skirmish AI flying them; enemy helicopters (the Resistance has none;
  helicopter against helicopter untested); low helicopters hidden from sight by terrain (H-9); the heavy MG's air
  reach (H-11).
- **Left for the engine:** Resistance aircraft (so the Sentinel's radar missiles and the
  overhead pattern meet enemy jets in real matches), the skirmish AI flying aircraft (and
  playing the Sentinel), per-weapon rearm times, a map wind, sim terrain craters and trees
  knocked down by bombs (D13, later).
- **Not engine work:** the airstrike visuals (CS, CT), the crash and ejection visuals (C7)
  and the jets' views are the client's; the bomb-smoke flipbook bake (D1 in the VFX plan)
  waits for the owner's go. The client pins only simcore `main` commits.

## Next engine milestones

- The skirmish AI playing the Sentinel (the Watcher, the jets).
- Resistance aircraft (the jets plan's later nights).
- An economy (credits) behind the existing no-op charge and refund hooks.
- Deterministic multithreading, only if the benchmarks demand it.
