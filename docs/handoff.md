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
    slope, forest and road-kind speed, arrival and stuck detection.
  - Movement box push-out and wall-ahead queries use the wall-reach buckets, preserving box-index order
    even after chained pushes. Routing refresh reach derives from `NavGrid.MaxWidth`;
    `MovementBoxTests` and `RoutingReachTests` cover full-scan equivalence and partner-cache invalidation.
  - Stuck units try bounded local detours around stationary body clearance before repeating their old route;
    group flow mode and formation slots remain intact. `unit_ai.json` controls the search budget/radius/cost/retry.
    `SimRun -stuck-bench` records ten-minute seeded QA, on the battle map or `-map flat`.
  - Supply uses fresh post-production recipient buckets for eight or more active suppliers, preserving
    ID order and scarce-stock allocation. Smaller cases scan recipients; depot lookups scan only suppliers.
    `SupplyIndexTests` compares against the original full scans. No content or replay change.
  - The 10,000-unit bench is at about 15–17 ms tick p99 (M3 Pro, 2026-10-20).
  - Maps up to 8,192 cells a side (2026-10-25, `TerrainMap.MaxSide`): a tick costs the units, not the map's area;
    6,000 x 3,000 holds about 0.9 GB with routing, fog and a battle; big maps prepare with
    `PrepareRouting(everySector: false)`; the first far order of a class over unbuilt sectors is a spike of up to a
    few seconds there (design.md, "Map size": the follow-up is a second routing level).
- **Terrain:** an optional heightfield (corner heights), slope classes per
  movement class, roads with a kind (highway, paved, gravel, dirt: speed and
  path cost per class), forest density, pads, water (rivers, lakes, the sea:
  impassable to every ground class, sight and shells at the surface, blasts in
  it crater-free and flagged `Water`; bridges deck it), line of fire,
  crest shielding, and the high-ground range bonus. Flat maps keep the old
  behaviour and hash.
- **Economy pieces:** supply depots with finite stock, depot reload costs per
  weapon, and cargo trucks.
- **Building and producing:** construction (sites, stages, workers), and the
  vehicle garage (a production queue, 2 bays, 4 doors with a seeded random
  door, rally points).
- **Skirmish scouting:** the normal waves remember visible enemy structures, forget only a seen-empty cell,
  and explore reachable 16-cell probes with separate reservations for idle wave members. Only the side's own
  fog/explored history is read; passive structures receive explicit attacks once visible and in range.
  `RegionSizeCells` and `RescoutEveryTicks` configure searching. Sentinel Watcher/Overseer missions remain follow-up work.
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
- **The stealth bomber** (B-2 type, 2026-10-22, branch `b2`; `docs/design.md`, "Stealth bomber" and "The heavy bombers'
  exclusive group", decisions 194 to 211): `stealth_bomber` (class XL, the `stealth` air class: 34% a missile hit, 3 hits;
  700 m, signature detect 25 / lock 300 / kill 70: no sensor sees it in flight at 700 m, decision 194; cruise 15 cells/s; 8
  flares), two bomb weapons the player picks between (`CommandKind.SetBombWeapon` 37, `Unit.BombWeapon`, `World.BombWeaponsOf`,
  a fire mode each): 16 `stealth_bombs` (`delivery.precision`: no scatter, each bomb steered onto its own point; `all16` /
  `group4`; `delivery.aims` 16; `delivery.tier` 7) and 2 `bunker_busters` (`both` / `single`; `weapon.penetrator`: everything
  in 3 cells destroyed whatever its armour, the units in a tunnel node there killed, the entrance collapsed into rubble;
  `World.TunnelKills`, `World.TunnelCollapses`, `CraterKind.Deep`, `World.Craters`). The heavy bombers' exclusive group
  (`content/mvp/exclusive_groups.json`, archetype `exclusive`; `World.ExclusiveOf`, `Player.ExclusiveLocks`): one at a time,
  the type locked by the first built; production refuses the rest. `StealthBomberTests` (21). Follow-ups: decision 194 (it is
  invisible in flight: the owner's height and signature together); no skirmish AI use.
- **Logistics** (2026-10-22, branch `logistics`; `docs/design.md`, "Logistics", decisions 212 to 236): the Sentinel's `airlifter`
  (a C-5 type, class XL, the `airlifter` air class: 4 missile hits; 300 m, 13 cells/s; unarmed, 30 flares) carries 4 containers of
  2,500 (`cargo_pallets`: a weapon with an `airdrop` block), loaded free at the main air base (unlimited `airbase_supply`; 30 s in a
  heavy bay). `CommandKind.Airdrop` (38) flies a 150 m pass over a drop zone (`all4` / `single`; scatter; a pallet in the zone fills
  its store, outside it is lost); `Deliver` (39) lands it at a forward airbase to unload into its stock (7.5 s a container), then
  home to load. The `facp` (field ammo point: stock 0 to 6,000, rearms its ground units within 12 cells), the `supply_stash`
  (storage, 0 to 15,000) and the `forward_airbase` (a compact core type: 7-segment runway, 2 stands for any class, a 2-bay field
  garage, no production, 8,000 to 20,000 stock that every service draws on, at least 120 cells from the main base). Cargo trucks
  `LoadCargo` (40) at a stash, a forward airbase or the main base and unload at an ammo point, a stash or a forward airbase. Code
  `Sim/Systems/AirdropSystem.cs`, `LogisticsSystem.cs`; queries `World.LogisticsEvents`, `StockOf`, `DropZoneOf`, `CargoAboard`,
  `AirfieldStore`, `AwayFromAllows`. `LogisticsTests` (20 tests, 21 cases). Follow-ups: no capturing (owner: later), no skirmish AI use, no
  automatic truck shuttle.
- **The Humvee** (HMMWV type, 2026-10-24, branch `humvee`; `docs/design.md`, "Humvee", decisions 237 to 250): the Sentinel's
  `humvee` replaces the technical in its roster and lobby (the Resistance keeps the technical): `medium_vehicle`, 400 HP, speed
  40, 500, 15 s in either garage; `humvee_mg` (14 at 7 cells, ground only, on the move; the `heavy_bullet` warhead: 125% against
  infantry); a driver plus 5 `infantry` riders who get out 0.4 s apart (the new crew `unloadInterval`; 0 keeps the
  all-at-once unload). Smoke launchers, a generic `smoke` effect for the tanks next: `CommandKind.Smoke` (41) pops a 12-cell
  screen for 10 s (2 charges, refilled where weapons rearm) that blocks sight lines through it and breaks guided missiles'
  locks (`Sim/Systems/SmokeSystem.cs`; queries `Smokes`, `SmokeEvents`, `SmokeOf`, `SmokeAt`, `InSmoke`). `HumveeTests` (21).
  Follow-ups: no skirmish AI use; no wind.
- **The main battle tank** (M1A2 type, "Abrams", 2026-10-24, branch `abrams`; `docs/design.md`, "Main battle tank", decisions
  251 to 271): the Sentinel's `main_battle_tank` (`armored_vehicle`, 2,500 HP, speed 30, 2,500, 45 s at the military garage
  only: production `excludeTags`), crusher 3 (soldiers and the light cars: `crushable` 2). Its 120 mm gun's 40 rounds are two
  weapon effects sharing one stock (`sharedAmmo`): HE (`he_mpat`, 250, 3-cell splash) or the dart (`apfsds`, 900) by target;
  the MGs (`tank_mgs`) are `coaxial` (their own reload, alongside the gun, unlimited). Generic, opt-in: directional armour
  (archetype `directionalArmor`: 40 / 80 / 130 / 150% front / side / rear / top; `Sim/Systems/ArmorSystem.cs`,
  `World.ArmorHits`) and active protection (the `aps` effect, Trophy: 3 charges, a chance by the threat's speed and launch
  distance, one engagement a second, against `apsThreat` weapons; `Sim/Systems/ApsSystem.cs`, `World.ApsEvents`, `ApsOf`).
  Smoke launchers. `AbramsTests` (25 tests, 29 cases). Follow-ups: drones and Trophy (owner question); no skirmish AI use; balance against
  the Resistance's anti-tank weapons (decision 271).
- **The light tank** (M10 Booker type, "Booker", 2026-10-24, branch `booker`; `docs/design.md`, "Light tank", decisions 272 to
  293): the Sentinel's `light_tank` (`armored_vehicle`, 1,400 HP, speed 38, vision 9 cells, 1,500, 30 s at the military garage
  only, tags `tank` and `airliftable`), the Abrams' schemes reused: `light_tank_105mm` (35 shared rounds, HE 170 or the dart
  600, 18 cells, 6 s), `tank_mgs`, directional armour 60 / 100 / 140 / 160%, smoke, no Trophy. Against the Abrams 11 / 6 / 4
  hits (front / side / rear; it takes 3 / 2 / 2); every Resistance launcher in one dart. The airlift: the airlifter's new
  `airlift` block (`AirliftSpec`, `Sim/Systems/AirliftSystem.cs`): `Enter` (4) boards 2 at the main air base (10 s each, the
  containers come off: tanks or containers, never both, `Unit.Lift`), `Deliver` (39) lands them at a forward airbase where they
  drive off to the field garage's rally point, `Exit` on it parked lets them out, lost with it; `World.AirliftEvents`,
  `AirliftAboard`, `AirliftOf`, `AirliftRamp`, `AirliftBoardSpot`. No new CommandKind. `BookerTests` (15 tests, 34 cases),
  `AirliftTests` (9 tests, 15 cases). Follow-ups: no skirmish AI use.
- **The howitzer** (M777 type, "Howitzer", 2026-10-24, branch `howitzer`; `docs/design.md`, "Howitzer", decisions 294 to
  313): the Resistance's `howitzer`, the first structure that fires: 10 x 10 cells, built by a worker (40 s, 1,500), 1,200 HP
  of building armour, 4 cells of sight. A crew seat on a structure (`crew`, one seat): any soldier of its side; silent and
  unorderable without him; 15% of explosive hits reach him (`crew.occupantDamage`); destroyed, he bails out by the crew rule.
  `howitzer_155`: 15 shells of 300 (`heavy_rocket` warhead), 3.5-cell splash, 15 to 80 cells from its centre (structures'
  weapons fire from the centre), 8 s, 10 cells/s on a 50% arc, scatter 1 to 3 cells, `rocket` interception class; a slow
  turret (archetype `traverseTicks` 8). Generic weapon fields: `truckReload` (cargo trucks sent with Unload or parked within
  15 cells hand a shell every 2 s for 20 supply and keep the rest: a truck's load is now 0 to 400), `reveal` (each shot spots
  it for every enemy for 200 ticks), `groupRadius` / `groupStructure` (auto targets with the biggest group first). Shift+G
  queues ground points for any ground unit (`Unit.GroundQueue`). Code `Sim/Systems/HowitzerSystem.cs`; queries
  `EmplacementOf`, `EmplacementEvents`, `RevealLeft`, `RevealedBy`, `FeedingOf`, `CanFeed`, `ShotAt`. No new CommandKind.
  `HowitzerTests` (24 tests, 25 cases). Follow-ups: no skirmish AI use; no C-RAM type to intercept its shells but the
  Patriot type (decision 311).
- **Night fixes** (2026-10-24, branch `night-fixes`; `docs/design.md`, decisions 314 to 316): two Bookers sent to board a parked
  airlifter together both board from any side (the board range counts from `World.AirliftBoardSpot`, the nearest cell a vehicle
  can stand on to the ramp point; in the heavy bay the second used to stall 4.1 cells off); an airlifter with vehicles aboard
  stays parked, held, after a Rearm order's service and leaves only on an order (replaces decision 292); no inflow pushes a
  store past its cap (a truck whose room shrank during its unload keeps what didn't fit; loading never drives a stock below 0).
  Tests `TwoBookersBoardFromAnyApproach` (7 cases), `ARearmOrderKeepsItParkedWithItsTanks`, `NothingPushesAStorePastItsCap`
  (2), `AContainerThatNoLongerFitsStaysAboard`, `ATruckLoadingNeverTakesTheStockBelowZero`. Golden replay unchanged.
- **The stealth multirole** (F-35 type, 2026-10-24, branch `f35-engine`; `docs/design.md`, "Stealth multirole (F-35 type)",
  decisions 346 to 360): the Sentinel's `stealth_multirole` (class M, built only by the main air base's hangar, no cap; the
  multirole's airframe, the `fighter` air class; 8 flares). Loadout modes (archetype `loadouts`, `loadoutSwitch`; `Unit.Loadout`,
  `Unit.StockMax`, `Unit.Signature`, `World.FlightOf` at the mode's speed): `stealth` (built in it: 2 heavy bombs + 10 radar
  missiles, 1.3x the multirole's speeds, 35 / 250 / 65) and `heavy` (10 + 2, the multirole's speeds, 80 / 130 / 90).
  `CommandKind.SetLoadout` (42) only parked at a station that rearms with free supply, 200 ticks, the new mode's full load
  (`World.LoadoutOf`). `f35_bombs` (1,690, 4 / 10 cells, tier 6, `single` / `pair`), `f35_aam`. Radar spotting (archetype
  `radarSpotting`; `Sim/Systems/RadarSpotSystem.cs`): an enemy `air_radar` (the mobile radar, the Osa) whose coverage it flies
  in is spotted for its side with a 2-cell disc, detected or not, until 200 ticks after (`World.RadarSpots`, `RadarSpotOf`;
  hashed). `StealthMultiroleTests` (19). Follow-ups: no skirmish AI use; the Sentinel's own radars carry no `air_radar` tag.
- **The faster airfield** (2026-10-24, branch `faster-airfield`; `docs/design.md`, "The faster airfield", decisions 361 to
  372): every airfield type's content `tempo` (leave 130, return 150) flies the pushback to the takeoff roll 1.3x and the flight
  home to the park-in 1.5x faster, time-scaled (the same ways and pattern, sooner; `AirOps.Pace` for renderers); the approach
  sequence (`Airfield.Approaches`, hashed): the next is cleared from the hold once the one ahead left its join, leaves its join
  once that one is down and lands once it turned off (4 jets home together: 200.5 -> 101.6 s); tighter, closer holds with
  `holding.reach`; the helicopters' `rotary.tempo`. `AirfieldTempoTests` (9). Not built: a linked field's runway for overflow,
  intersection departures.
- **Bridges** (2026-10-25, branch `bridges-engine`; `docs/design.md`, "Bridges", decisions 373 to 387): `bridge_stone` (3 x 8
  cells, 2,500 HP, paved), `bridge_concrete` (5 x 12, 3,500, paved), `bridge_metal` (9 x 18, 4,500, highway), archetype block
  `bridge`. The map places them (`bridges` in the terrain JSON: `kind`, `cell`, `dir` `ns` / `ew`; `TerrainMap.BridgeSites`, put
  up by `AttachTerrain`; checked: ends dry, spanning water; `TerrainLoader.LoadMap(path, rules, content)`), the sandbox too;
  players never build one. Neutral for good, blocking nothing; its road on every footprint cell, its water cells a flat deck.
  Stages by HP (70 / 40 %); at 0 HP the span falls: deck lifted, everyone on it lost, paths re-planned, the ruin stays at 1 HP,
  no target; builders of either side repair it (Repair order), the deck back at 40 %. `World.Bridges`, `BridgeAt`,
  `BridgeEvents`, `Unit.Bridge`, `CanPlaceBridge` / `BridgeRefusal`. `BridgeTests` (36). Follow-ups: no skirmish AI use; the
  deck's height is the water surface in the sim.
- **Alleys and footpaths** (2026-10-25, branch `roads2-engine`; `docs/design.md`, "Alleys and footpaths", decisions 388 to 396):
  two more `roads[].kind` values. `alley` (old-town street): only the foot class enters it, every other ground class finds a wall
  (cost and speed 0 in the terrain cost grids, so HPA*, flow fields and movement need no special case); foot pays paved's cost,
  no speed change. `footpath`: blocks and slows nobody, foot routes (groups too) prefer it at paved's cost. A vehicle road wins an
  overlapping cell. Second per-cell byte `_flags2` (bits 0-1 the kind, 2-7 spare), allocated and hashed only on a map with a foot
  kind (old maps hash as before, pinned). No structure on an alley, vehicles spawned or produced on one move to the nearest cell
  that is not (`World.IsAlley`, `WorldRoads.cs`). `RoadKind.Alley` = 5, `Footpath` = 6, `TerrainMap.IsFootOnly`. `RoadKind2Tests`.
  Follow-ups: a vehicle unloaded or a unit pushed onto an alley is only moved off by movement's push-out; garage service exits do
  not skip alleys.
- **Trees as obstacles** (2026-10-25, branch `tree-trunks`; `docs/design.md`, "Trees as obstacles", decisions 439 to 450):
  standing trunks worked out from the forest (`TreeTrunks`: one per 2 x 2 slot at most, a pinned hash, chance per density in
  `trees.trunkChance`, a bit a cell, not in the digest), folded into the terrain's per-class costs: infantry ignore them, jeeps
  and light trucks (`trunks: avoid`) find their cells walls, tanks and heavy trucks (`push`) push them over (30% speed, 1.5 s
  of contact, the tree falls the way they face; the trunk lies 4 cells for 30 s, a wall to every vehicle but the pushers,
  `crossFallen`). Dense forest no longer stops the pushers and now stops the cars. `World.FallenTrunks`, `Stumps`, `TreePushes`, `TreeEvents`, `ClearTrees`; `TerrainMap.HasTrunk`,
  `TrunkOffset`, `TrunkPoint`, `Fallen`. Code `Sim/Trees.cs`, `Sim/Systems/TreeSystem.cs`; `TreeTrunkTests`. Follow-ups:
  decisions to confirm (crossFallen above all); a unit whose goal a fallen trunk shuts off does not wait for it to go.
  Strikes blow trees down (decisions 451 to 457): the warheads in `trees.fell` (rules) fell every trunk within a percent of
  their splash radius (at least 1.5 cells), away from the impact; a falling trunk takes 10% of max HP from infantry under it.
  Routing now drops only the caches of the classes a change alters (a carpet over a wood: p99 6.2 -> 0.31 ms). `TreeBlastTests`,
  `SimRun -spike-bench carpet,carpet-nofell`. Fire (burning, a burnt cell's density dropping, smoke) is task C.
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

World hashing keeps the original word stream and digest, with batched rounds and a smaller per-unit hot loop.
`HashEquivalenceTests` checks the original implementation; `HASH_COMPARE=1 SimRun -bench ...` checks it each measured tick.

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
- **Left for the engine (big maps):** far-route planning without the cold-sector spike on 6,000 x 3,000 and up (a
  graph of sectors or a landmark heuristic; design.md, "Map size").
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
