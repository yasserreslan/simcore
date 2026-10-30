# RTS Game Design System

Updated October 16, 2026

The first playable game is a Resistance-vs-Resistance 1v1 skirmish on one 96×96 map against a tunable test bot. It runs on a deterministic simulation that the renderer only draws. The renderer is a real-time 3D Godot client; units, buildings and props are 3D models built by scripts and played as they are. Every decision here was agreed step by step, first in the illustrated [Resistance MVP walkthrough](resistance-mvp.html) (download and open it in a browser; it predates the move from pre-rendered sprites to real-time 3D, so its illustrations show the earlier sprite look) and since then in the project's decision log. Setting: the real world today, with fictional countries, groups and leaders.

The game is a Generals-style modern RTS: tilted perspective camera, trackpad-first C&C controls (left-click selects, moves and attacks; pinch or scroll zooms), garrisonable buildings, a rocket fleet that depends on supply.

## Visual spec

Real-time 3D, "real but low poly, like Generals": realistic proportions and materials, smooth shading with hard edges only where real, and just enough exaggeration to read at RTS distance. The bar is polished and carefully crafted, not very low poly. Every asset follows one camera, one light and one scale, so assets from different artists or scripts look like the same game. (The game's art bible, kept with the art, holds the full rules; this section is the engine-facing summary.)

| Rule | Value | Why |
| --- | --- | --- |
| Rendering | Real-time 3D in Godot 4 (.NET), capped at 60 fps with vsync | Models scale with zoom and animation follows sim speed; pre-rendered sprites were tried and rejected (too small, wrong walk cadence) |
| Camera | Perspective, 35° field of view, about 52° down when zoomed out and 34° in a close-up; zoom from 7 m to 120 m | The classic tilted RTS view, close enough to admire a soldier, far enough to see a base |
| Tactical preset | Perspective 35°, distance about 66 m, pitch about 40° below the horizon, yaw about 0.5 rad (about 29°, a diagonal view of a city grid) | One agreed framing for official screenshots and lineups; whether it becomes the default gameplay camera is still open |
| Scale | 1 cell = 2 m; a soldier is about 1.9 m (about 0.95 cell tall); real-world metres everywhere | Keeps models, speeds and ranges honest; the 96×96 map is 192 m square |
| Footprints | Infantry about 0.4 cell, light vehicle 1 cell, heavy vehicle 1.5 cells, buildings snap to cells (2×2 up to 23×6) | Keeps pathing on a cell grid |
| Readability | Head and helmet +15%, weapons +25%, slightly thicker limbs, applied in the model | Roles read at a glance from far away |
| Key light | One sun from the top-left (north-west), 60° up, warm, soft real-time shadows, plus a procedural sky for ambient light | Shadows fall bottom-right on every asset, and stay about as long as the unit is tall |
| Frames | Skinned animations sampled at 30 fps and driven from sim state (speed, firing, reloading) | Feet stay planted; animation never leads the sim |
| Team color | A flat `TEAM` material on real parts (infantry: helmet band and left armband; vehicles: door stripes and roof panel), tinted per player at runtime; 8 player colors | One model serves every player |
| Triangle budgets | Infantry about 8 to 12k, vehicles about 30 to 50k with crew, buildings 40 to 80k per damage stage, 2K baked textures | Polished, but 60 fps with dozens of units on screen |

**Color hierarchy.** Terrain is warm and desaturated. Unit bodies are mid-saturation neutrals (arid multicam, coyote gear, matte black weapons, sun-faded sand paint). Team color is the most saturated element on a unit. Effects (muzzle flash, explosions, tracers) are the brightest thing on screen.

**Silhouette rule.** Each role must be identifiable from its silhouette alone at the strategic zoom: tanks low and wide, artillery a long barrel, anti-air vertical barrels, harvesters a big box, infantry upright. If two units can be confused when shown as black shapes, redesign one.

**Setting rule.** Real-world gear on fictional groups. No real flags, patches, insignia, slogans, logos or any text on assets.

**Rendering boundary.** The simulation never reads the renderer and the renderer never writes the simulation, so drawing can never cause a desync. Hidden units are drawn only when the player can see them; units hidden behind scenery show as a flat team-colored silhouette.

## Art pipeline

Every model is built by a script, so adding a unit takes hours, not weeks and every asset can be rebuilt identically. There are no third-party models, textures or animations.

1. **Build.** A Blender (Python) script builds the model at real-world scale (1 Blender unit = 1 metre; the game maps 1 cell to 2 m): geometry, shared material library (base paint, fabric, metal, rubber, glass, one flat `TEAM` material), rig and animations for characters, named nodes for everything the game must find (weapons, muzzles, wheels, turrets, doors, window firing points).
2. **Bake.** Procedural materials (camo, grime, wear, ambient occlusion) are baked to base-colour textures (2K for bodies, vehicles and building stages; smaller for weapons and props). No directional shadow is baked in.
3. **Export.** One glTF binary (`.glb`) per asset plus a JSON sidecar: units, up axis, facing, the animations (frames, loop flag, ground speed, event frames), weapon and muzzle nodes, named nodes, notes (footprints, fire points, tube order). Units face -Z, origin on the ground.
4. **Verify.** A script re-imports the glb in a fresh Blender and checks animations, `TEAM`, named nodes and baked poses; a style checker validates every export against a machine-readable style file (palette, budgets, naming rules, banned content).
5. **Import.** The Godot client loads the glb and sidecar at runtime (no import step) and reads values from the sidecar: it never hard-codes art values. Stand-in procedural shapes keep the game playable while art is missing.

Contract rules that keep the pipeline safe: unique node names; a flat material named exactly `TEAM`; visibility that changes during an animation is keyed bone scale, every animation keys every visibility bone and no track is constant (Godot strips unchanging tracks).

File naming: `art/export/<asset>.glb` and `<asset>.json`, snake_case.

Animations for infantry (idle, aim idle, walk, run, shoot, reload, hit, three deaths, plant, launch, plus launcher and worker animations) are authored in the same scripts and shared by every class. Crewed vehicles carry their crew (gunner, driver) and wrecks (`body_destroyed`) in the same file.

## MVP: map, economy and base

One hand-made 96×96 map, bases in opposite corners, won by destroying every enemy structure and defense.

| Topic | Decision |
| --- | --- |
| Map | 96×96 cells, fog of war (unexplored dark, explored-but-unseen grey), one central oil field |
| Gold mines | 2 visible per base + 4 hidden; the map marks possible spots and the match seed picks which hold gold |
| Gold | Workers mine and carry each load back to the HQ; mines slowly run dry |
| Oil | A derrick on an oil spot plus a refinery within range of it; slower and costlier than gold, pays more over time |
| Starting cash | A grant at match start |
| Builders | Only workers build structures, for now |
| Power | None |
| Starting defenses | Pre-placed anti-infantry and anti-armor emplacements at each base; exact set to be decided |
| Win | Destroy every enemy structure and defense, starting defenses included (Phase 1, before structures exist: a player with no soldiers left, on foot or driving, is defeated) |
| Neutral buildings | Garrisonable city blocks stand on the map (see [Buildings](#buildings-garrisons-and-destruction)) |

| Structure | Does | Requires |
| --- | --- | --- |
| HQ | Trains workers, receives gold | Start |
| Barracks | Trains the four infantry classes | HQ |
| Vehicle yard | Builds the six vehicles | Barracks |
| Supply depot | Rearms launchers and drone kits out of a finite stock (1000 supply); faster reload nearby; the only place a rocket truck reloads; refilled by cargo trucks | HQ |
| Tunnel entrance | A node in the tunnel network | Barracks |
| Derrick + refinery | Oil income | Supply depot |

## Combat rules

Damage dealt = weapon damage × the warhead-vs-armor multiplier below, so every unit has a clear counter. "—" means the weapon can't target that kind of unit. No heavy armor in the mirror match.

| Warhead ↓ / Armor → | Infantry | Light vehicle | Building | Drone | Medium vehicle | Armored vehicle | Parked aircraft |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Bullet | 100% | 40% | 10% | 15% | 40% | 20% | 60% |
| Sniper round | 150% | 20% | 5% | 15% | 20% | 10% | 30% |
| Rocket (AT missile, launchers) | 40% | 120% | 80% | — | 120% | 120% | 120% |
| Rocket truck rocket | 67% | 133% | 80% | — | 133% | 35% | 133% |
| Explosive (traps, FPV drones) | 100% | 100% | 60% | — | 100% | 100% | 120% |
| Heavy rocket | 100% | 100% | 200% | — | 100% | 120% | 120% |
| Heavy missile (twin missile) | 100% | 100% | 220% | — | 100% | 100% | 120% |
| Ballistic (tactical missile) | 100% | 100% | 220% | — | 100% | 170% | 120% |
| ATGM (Kornet) | 15% | 100% | 45% | — | 55% | 100% | 100% |
| Mortar bomb | 100% | 45% | 30% | — | 45% | 10% | 80% |
| Drone charge | 100% | 100% | 100% (20% of max HP) | — | 100% | 100% | 100% |
| Air strike (Watcher missile) | 100% | 60% | 120% | — | 60% | 25% | 100% |
| AP mine blast | 100% | 10% | 5% | — | 10% | 2% | 40% |
| AT mine blast | 100% | 100% | 30% | — | 100% | 25% | 100% |
| AA missile | — | — | — | 100% | — | — | — |

Medium vehicle is the rocket launchers' class (light, medium and heavy rocket trucks, the
heavy rocket truck and the twin missile truck); every warhead but the ATGM treats it like a
light vehicle. The technical and the cargo truck stay light vehicles. Armored vehicle is the
infantry carrier's class (2026-10-18): tuned with its 1200 HP so that no weapon's whole first
salvo kills it, even with every round a direct hit, except the tactical ballistic missile and a
full 12-rocket heavy salvo (see "Infantry carrier" below). Parked aircraft (2026-10-19, airfield
M3b) is the armor of an aircraft on the ground at an airfield (its aircraft block's `groundArmor`):
a thin airframe full of fuel, a little softer than a light vehicle; it is never an attack target
there and only splash reaches it. In the air the same aircraft keeps its drone armor.

- Rockets and explosives deal splash; launcher rockets fly with travel time and scatter, so they can miss moving targets.
- Traps (booby traps and the assault soldier's mines) are hidden from the enemy, fire once when an enemy ground unit comes close, and ignore drones. Only enemy snipers, scout drones and observation posts reveal them (2026-10-18; engineers no longer do); once revealed, any unit can shoot them, which clears them without a blast. The full rules are under "Traps" below.
- **Rockets and missiles collide.** They fly along their aim line and go off at the first unit, structure or rock they touch (full damage to what they hit plus splash around the point), or on the ground at the aim point if nothing is in the way. Friends count; the shooter and anyone inside a vehicle don't. Launcher rockets fly an arc and only collide in the last 2 cells; one coming down over a structure's footprint hits its roof.
- **Leading targets.** Rockets and missiles aim where a moving ground target will be after the flight time, so a target driving straight is hit and one that turns or stops can still dodge. Attack orders are always obeyed; automatic targeting has per-armor engagement limits (the AT missile auto-engages infantry only close up and prefers vehicles and structures).
- **Weapon modes.** Each unit has a weapon mode: `auto` (any weapon, the best for the target) or `primaryOnly` (only its first weapon). Anti-armor soldiers default to `primaryOnly` (RPG only: he keeps to his launcher and never switches to a sidearm); the player toggles RPG-only vs RPG + rifle in the command card.
- **Attack ground.** A force-fire order: units walk into range of a cell and fire at it until given another order. Splash hits whoever stands there, friends included.
- **Range anchors.** The RPG reaches 9 cells; a technical's heavy MG 6 cells. Small arms show thin, short muzzle flashes and thin white tracers in per-weapon bursts.
- All values are integers in the sim (percent as 0–100), never floats. Starting values for tuning (they live in the content data files); traps per soldier is set in playtests.

### Unit AI (2026-10-19)

What units do without orders, after Zero Hour's player rules (its open-sourced code: `AIStates`,
`AIGuard`, `AIGuardRetaliate`, `ActiveBody`, `SquishCollide`). Numbers live in the `unitAi` content
block (`content/mvp/unit_ai.json`); archetypes set `stance`, `pursue`, `leash`, `retaliateMax`,
`autoFireDeployed`, `crusher` and `crushable`. Code: `Sim/Systems/UnitAiSystem.cs`, `OrderSystem.cs`,
`CrushSystem.cs`.

| Stance | Fires on its own at | Moves on its own |
| --- | --- | --- |
| **Guard** (default) | spotted enemies in weapon reach | to retaliate (it and idle friends nearby), on the leash, then walks back |
| **Aggressive** | the same, and goes after spotted enemies within vision x 1.33 | chases on the leash, then walks back |
| **Hold ground** (the old Guard) | spotted enemies in reach | never |
| **Return fire** | only the enemy that last hurt it, in reach | never |
| **Hold fire** | nothing (orders only) | never |
| **Hide** (2026-10-25, concealment from the air) | nothing (orders only), exactly as Hold fire: a unit under trees gives itself away only when ordered | never |

- **Retaliation.** An enemy's shot or blast hurting a unit in Guard or Aggressive (even fatally)
  calls it and its idle friends within 8 cells: each that pursues and can hit the shooter goes after
  it. Not for flying shooters, shooters beyond 20 cells, units with an order (attack-move and Guard
  count as idle), units in a building or vehicle, or archetypes that don't pursue (launchers, the
  mortar, air defence, engineer, worker: `pursue: false` or a deploy or air vision). A shooter in a
  building makes them go for the building. One call per victim per second (a salvo calls once).
- **Leash.** The chase starts from where the unit stood (its guard point; on a Guard order, the
  post). It closes to weapon reach like an attack and ends when the target dies or hides in a
  vehicle or building, gets farther than vision x 2.2 from the point, or after 15 s. A shooter it
  can't see: it heads for where the shot came from (an attack-move: it stops for targets in reach),
  while that is within the leash. Then it engages anything else spotted within vision x 1.8 of the
  point, or walks back, scanning that circle every second, and idles. Stop, any order, or a stance
  that doesn't move ends it.
- **Attack-move** (A + click on the ground). A group move; each unit fires at spotted enemies in
  reach on the way (no chase), fixed weapons stop for them and carry on, turrets fire on the move;
  it retaliates on the way and ends on arrival. An attack target lost in the fog becomes an
  attack-move to where it was last seen.
- **Guard order** (Option+G on a cell or one of your units). Each unit keeps to its post (a group:
  its formation slot; a guarded unit: within 3 cells, the circle moving with it), engages the best
  spotted enemy within vision x 1.8 of it every half second, chases on the leash and walks back.
  It lasts until another order.
- **Scatter** (Option+S). Each selected unit moves straight away from the group's centre by
  4 x its radius, at least 2 cells (one on the centre goes the way it faces).
- **What units pick.** The enemy that hurt it within 15 s, then threats (they can hit it) before
  harmless units, then targets a weapon engages freely before restricted ones (`autoRange`), then
  the weapon's priority list, nearest, lowest ID. It keeps its target, but drops a harmless one for
  its attacker. Structures that can't fight are left to orders; armed ones and enemy-held
  buildings are picked. Only spotted enemies, always.
- **Launchers.** A set-up mortar fires on its own (it never sets up by itself); the heavy MRL,
  heavy rocket truck, twin missile, tactical missile, rocket jeep and drone truck fire on orders
  only; light and medium rocket trucks fire on their own in reach but never chase.
- **Crushing.** A vehicle rolling forward kills enemy infantry it drives into (touching, heading
  toward it): trucks, launchers and the carrier crush; the dirt bike doesn't. Friends, passengers,
  garrisons and vehicles are never crushed. The AI doesn't chase infantry to crush them.
- **Carrier passengers** don't fire from inside (a closed carrier).
- **The computer opponent** (`SkirmishAi`) puts its fighters in Aggressive, on Guard posts round
  its base, and sends its waves by attack-move.
- **Determinism and cost.** Integer distances only, no random draws, ID order and ID tie-breaks;
  radius queries go through the spatial index. Scans of the guard and Aggressive circles (and the
  rescan after a chase) run every 10 ticks phased by unit ID (`(tick + id) % 10`). Vision by
  distance is staggered too: a viewer looks around every 4 ticks (`(tick + id) % 4`,
  `World.VisionTicks`) or as soon as it enters another cell, and what it saw stays spotted until it
  looks again, so spotting and losing sight lag by at most 3 ticks (0.15 s). A unit without a
  target searches its reach when it has just looked around (or on that phase); a unit whose target
  just became invalid searches at once, and a valid target is still checked every tick. A chase's first route waits up to a second, staggered by ID, so a
  fight that sets off many chases spreads their path searches.

### Infantry squads (2026-10-28, phase S1)

Infantry form squads of up to 10 that the player orders as one (the owner's spec of 2026-10-27, "Infantry squads", game repo;
approved). S1 is the first of five phases: forming, merging, adding, detaching and disbanding; the leader; **March** and
**Assault** with fixed slots; one path per squad with every member following his slot; commands through `World.Issue`, the
squad state hashed. Hold and position picking (S2), Ambush (S3), the air ambush (S4) and the computer opponent's squads (S5)
come later; the state is built for them (modes and states are append-only enums, the roles and the content block are there).
S2 is built: see "Squads: Hold and position picking" below; S3 too: "Squads: Ambush"; and S4: "Squads: Air ambush".
Content: `content/mvp/squad.json` (the `squad` block) and four class tags (`medic`, `rpg`, `atgm`, `manpads`). Code:
`Sim/Squad.cs`, `Sim/Systems/SquadSystem.cs` (a pass right before the path system: `StepPhase.Squads`), `Model/SquadRules.cs`.
Tests `SquadTests` (24 tests, 28 cases); bench `SimRun -bench-squads 50`. Numbers marked (spec) are the spec's; the rest are
rows 601 to 620 of "Decisions to confirm".

- **Who joins (spec).** 2 to 10 infantry of one player (`size`), any mix of classes: the `need` tags (`infantry`), none of the
  `never` tags (`worker`, `pilot`), on the map (not inside a vehicle or building), able to walk (`World.CanJoinSquad`). Vehicles,
  drones and aircraft never join.
- **Form Squad** (`CommandKind.FormSquad` 45, the client's Option+Q; spec). None of the listed soldiers in a squad: squads of 10
  in ID order (11 selected: a squad of 10 and one loose; 12: 10 and 2). Members of one squad and loose soldiers (**add**): the loose
  ones join it in ID order up to 10, the rest stay loose. Members of several squads (**merge**): into the lowest-ID squad if they
  are 10 or fewer together, then the loose ones join; more than 10: nothing changes (`SquadEventKind.MergeRefused`). Forming
  stops everyone; the squad takes its leader's stance and facing and forms round him in March.
- **Detach** (46) and **Disband** (47) (spec). A detached soldier is loose with the squad's stance and acts alone where he is
  (his squad move ends; another order of his own goes on). Disband leaves everyone loose and idle, in place.
- **Losses (spec).** A member who dies leaves the squad in the same tick's cleanup (`SquadEventKind.Lost`); at one member it
  disbands.
- **The leader (spec).** The lowest-ID member whose role leads: not a medic, sniper or mortar (`noLead`); none: the lowest ID.
  When he dies the next one takes over in the same tick (`SquadEventKind.Leader`). He is not a special unit, only the one whose
  slot is the anchor at rest (on the move the head's slot goes by place: "Squads: slots by place").
- **The stance (spec).** One for the whole squad (`Squad.Stance`): a stance given to any member is the squad's and every member's.
- **Roles (spec, by tag).** `roles` maps class tags to roles, first match wins, the rest are Line: `medic` Medic (the Sentinel's
  assault medic), `sniper` Recon, `rpg` Flank (both anti-armour presets), `atgm` Long flank (the Kornet team), `manpads` Air guard,
  `mortar` Support; riflemen and engineers are Line. `World.SquadRoleOf`.
- **One path, slots round an anchor.** The squad plans one path (the leader's class, from the anchor to the goal; HPA* refined
  a few legs at a time, as a unit's own route). Its **anchor**, the leader's slot, walks the path at the squad's speed (the terrain
  under it slows it as it would the leader). The other members' slots are laid out round the anchor, and each member steers to his
  slot: straight when the slot's cell fits him and the way there is clear, else on a route of his own toward it (toward the anchor
  when the slot's cell does not fit), planned at most once a second. A member who falls more than 4 cells (`regroup`) behind his
  slot makes the squad wait for at most 3 s (`regroupTicks`), then it walks on; one ahead of his slot never does (SQ-ORDER: he
  waits for his slot, see "Squads: slots by place").
- **March (spec).** (The role order below is the shape at rest; on the move the members swap slots by place: "Squads: slots by
  place".) A file **1.5 cells apart** (`march.spacing`) along the anchor's trail (its walked points, half a cell apart:
  the file walks in the leader's footsteps and bends with the path), at **the slowest member's speed** (`march.speed` 100). From
  the front: recon 5 cells ahead along the path (`ahead`), the leader, the flanks, the line with the medics in its middle, then the
  long flanks, the air guards (3 cells apart, `airGuardGap`) and the support (packed, 1 cell). Before the trail is long enough the
  file goes straight back the way it came (at forming: behind the leader's facing).
- **Assault (spec).** A line **across the facing, 2 cells apart** (`assault.spacing`) at **80%** of the slowest speed (`speed`).
  The leader in the middle, the line soldiers beside him (the shorter side, the right first), the flanks at the two ends; behind
  the line: the medic 3 cells (`medic`), the long flank 4, the air guards 5 cells back 3 apart, the support 7 back packed, recon 4
  back beyond the line's right end by 2 cells (`recon`, `reconSide`; a second one to the left). The facing is the way to the path
  3 cells ahead of the anchor; idle, the last one.
- **Moving and arriving (spec).** `CommandKind.Move` and `AttackMove` given to any member move the whole squad; its members carry
  the squad's order (`OrderKind.Move` / `AttackMove` to its goal), which the squad ends when the anchor reaches the end of its path
  (`SquadEventKind.Arrived`). Several squads in one order stand side by side across the move, 12 cells apart (`groupGap`); loose
  soldiers in the same order take the ordinary group move. The shape is rebuilt on arrival (the file along the last stretch, the
  line across the last facing) and kept while idle: a member pushed off his slot walks back.
- **Fighting (spec).** With no enemy in sight the squad keeps its shape. In a fight each member fights as the unit AI says
  (stance, retaliation, leash), but **his leash point is his slot**: he chases on the leash from it and walks back to it. On a
  squad Move members only walk (as on any Move); on a squad Attack-move a member stops to shoot what is in reach and chases what
  hurts the squad, and **the squad waits while any member fights**.
- **Orders of their own.** Attack, Attack ground, Guard, Scatter, Enter, Exit and Stop given to any member go to every member, who
  carries it out on his own; the squad is **loose** meanwhile (`Squad.Loose`, its move ended) and rebuilds its shape round where
  its leader stands once every member on the map is idle again (Stop: at once, round the leader). Abilities, Deploy and the like
  stay one soldier's.
- **Boarding (spec).** An Enter given to a member takes the squad into the carrier or building; inside it stays a squad (the members
  keep `Unit.Squad`); an Exit given to one member lets out every member in the same building or riding in the same vehicle (a
  driver stays at the wheel), and the squad forms up round its leader outside.
- **Mode** (`CommandKind.SetSquadMode` 48, `Command.Index`: `SquadMode` 0 March, 1 Assault). The shape is rebuilt at once, moving
  or not.
- **For the client.** `World.Squads` (ID order), `Squad(id)`, `SquadOf(unit)`, `Unit.Squad`; a squad's `Members` (ID order),
  `Leader`, `Mode`, `Stance`, `State` (`SquadState` Moving or Idle), `Order`, `Goal`, `Path` / `PathIdx`, `Anchor`, `Facing`,
  `Loose`; each member's slot (`SquadSlotPoint`, `SquadSlotCell`: the faint dot while Option is held); `SquadRoleOf`,
  `CanJoinSquad`; `World.SquadEvents` (formed, merged, joined, detached, lost, leader, disbanded, merge refused, arrived).
- **Determinism and cost.** Integer distances, ID order, no random draws, no scores yet (S2's position picking). The squads are
  hashed once one has been formed (games without squads hash as before); the slots are worked out from the members, their roles,
  the mode and the anchor, and are not hashed. 50 squads of 10 marching across a 256-cell map: the squad pass 0.09 ms of tick p99,
  less path work than the same soldiers in group moves (`SimRun -bench-squads 50`, `bench/squads_50.json`).

### Squads: Hold and position picking (2026-10-28, phase S2)

Phase S2 of the owner's squad spec (sections 3 and 5): **Hold**, the facing the client drags, the position picking with integer
scores and role weights in the `squad` content block, and garrisons counted as positions. Content: the `hold` part of
`content/mvp/squad.json` (optional: without it Hold is ignored). Code: `Sim/Systems/SquadHoldSystem.cs` (in the squad pass),
`Model/SquadRules.cs` (`SquadHoldRules`). Tests `SquadHoldTests` (14 tests, 16 cases); bench `SimRun -bench-hold 50`
(`bench/hold_50.json`). Numbers marked (spec) are the spec's; the rest are rows 633 to 652 of "Decisions to confirm".

- **The order.** `CommandKind.SetSquadMode` (48) with `Index` 2 (`SquadMode.Hold`, appended), `Cell` the spot and `Cell2` where
  the dragged facing ends (null: none dragged). No new `CommandKind`. The facing points from the spot to `Cell2`; the **threat**
  is that end, kept 4 to 30 cells from the spot (`threatRange`); without a drag the squad faces the way it walked there and the
  threat is 10 cells ahead (`threat`). A squad more than 12 cells (`radius`) from the spot marches there first (as March) and
  picks on arrival; nearer, it stops and picks at once. Several squads in one Hold stand side by side across the facing, the
  group gap (12 cells) apart, in ID order. Hold lays no mines (spec). A Move or Attack-move keeps the mode (spec): the squad
  leaves its positions (garrisons out at the doors, the mortar packs up), walks as March and holds again where it arrives,
  facing the way it went. March or Assault ends Hold the same way.
- **Candidates (spec).** Every free cell within 12 cells of the spot, the nearest first (by distance, then row, then column),
  at most 600 looked at (`cells`; the 12-cell radius holds 441): clear ground the squad's class can stand on and reach
  from where its leader stands (the routing's connected regions). Plus each garrisonable building its side may enter whose
  footprint comes within the radius, with room, and a line of sight from its windows on the threat's side to the threat.
- **Factors (spec, each 0 to 100).**
  - *Cover*: points for a wall (rock, a building or its rubble) next to the cell on the threat's side (the 3 neighbours toward
    it, 60), a tree trunk there (30), the cell in a grove (25) or dense forest (40), a crest hiding the cell from the threat (50:
    the ground between rises above the line from a soldier there to an eye at the threat); their sum, at most 100. A building:
    100.
  - *Trees*: 100 under canopy that hides from the air (`concealment`'s cover densities), else 0.
  - *Sight*: three lanes from the cell, at the threat and 6 cells either side of it (`laneSpread`), with the fog's line of
    sight (the ground's and the treetops' horizons, the forest budget); a lane ends at rock, a building or rubble and the map's
    edge. Half the score is the share of the lanes' cells seen out to the soldier's own reach (his sight or his weapon's, the
    longer, at most 30 cells: `laneMax`), half how many of the three aims he sees within that reach.
  - *Open*: the share of the lanes' cells seen that are open ground (one point) or road (another).
  - *Spacing*: the distance to the nearest teammate already placed, full at the role's `spread`.
  - *Safety*: the depth back from the threat side along the facing: 0 at the radius ahead of the spot, 100 at the radius behind.
  - *Range*: the threat within the soldier's ground weapon reach (plus a building's range bonus), 100 at three quarters of it,
    falling to 67 at full reach and 0 beyond it or inside its minimum.
  - *Near*: 100 on the spot, 0 at the radius.
- **Role weights (content).** A cell's score for a soldier is the sum of factor times his role's weight
  (`roles.<role>.weights`: cover, trees, sight, open, spacing, safety, range, near). Starting values: line 5 0 3 0 2 0 1 5 (cover
  facing the threat, near the spot), medic 5 0 0 0 1 2 0 6 (behind cover, central), recon 2 0 6 1 1 3 2 0 (the best overwatch),
  flank 3 0 4 4 2 0 3 1 (roads and open ground in reach), long flank 2 0 6 2 1 2 4 0 (a long lane toward the approach), air guard
  1 8 0 0 3 1 0 1 (under trees), support 3 0 0 0 1 3 3 0 (back, in range).
- **Hard limits (content).** `back`: the least depth behind the line soldiers placed before him (their mean; before any, the
  spot): medic 2 cells, support 10 (spec: 10+ back), recon and long flank 0 (not ahead of the spot), the others none. `apart`:
  [from anyone, from the same role]: 1.5 cells from anyone, air guards 8 cells from each other (spec). A soldier with no cell
  left within his limits takes the best one ignoring them (never another's cell); with none at all he holds where he stands.
- **The pick (spec).** Once per order (on arrival, or at once when near): members pick in the order recon, long flank, flank,
  line, air guard, support, medic (`order`), ID order within a role, each taking the best candidate left; ties to the lowest cell
  index, buildings after cells by ID. No random draws. The cells the owner's other holding squads hold count as taken.
- **Garrisons (spec).** A building candidate counts only for roles with `garrison` (line, flank, recon: riflemen, RPGs, the
  sniper; the medic, the Kornet team, the mortar and the Iglas stay out), only for a soldier its garrison takes and whose reach
  plus the building's range bonus covers the threat from its edge, and while it has room (its stage's capacity less those in it
  and those promised). The soldier is sent in (`OrderKind.Enter`); his pick's cell is the door nearest the scored point, where
  he stands while he can't get in (he tries again every second). One already in another building comes out to his position.
- **Holding (spec).** Each member walks to his cell (the slot point is the Hold cell) and stays until a new order; a set-up
  weapon (the mortar) sets up on its cell and fires on its own. In a fight each member fights as the unit AI says, **his leash
  point his Hold cell**. Orders of their own (Attack, Guard, Stop, ...) make the squad loose as in S1; once they are done the
  members walk back to their Hold cells (the picks are kept). A soldier who joins picks the best cell left (the others keep
  theirs); a lost one's pick goes.
- **For the client.** `Squad.Mode` Hold, `HoldSpot`, `HoldFacing` (the arrow), `HoldThreat`, `HoldPlaced`, `HoldPicks`;
  `World.SquadHoldPick(unit)` (his cell and building), `SquadHoldGarrisoned(unit)` (inside as part of Hold), `SquadSlotPoint` /
  `SquadSlotCell` (the Hold cell once placed), `SquadHoldScores(member)` (the debug view: every candidate with its factors, total
  and whether his limits allow it, as his role scores it with the teammates' picks taken); `SquadEventKind.Placed` (appended;
  `Other`: the cells scored).
- **Determinism and cost.** Integer scores, ID and cell-index tie-breaks, no random draws, no hash collections. The Hold state is
  hashed (spot, facing, threat, placed and waiting flags, the picks); the candidates and their factors are worked out from hashed
  state and are not. At most 2 squads pick in one squad pass (ID order); the rest wait a tick. 50 squads of 10 on a hilly,
  wooded 256-cell map given Hold in place, then a Hold 16 cells on (`SimRun -bench-hold 50`, Release, M3 Pro): the squad pass
  0.60 ms of tick p99 (gate 1 ms: met), the tick p99 +0.48 ms against plain moves; **one order's scoring 0.23 ms** (441 cells; at
  most 0.25 ms), of which building the candidates (mostly their sight lanes) took about 0.14 ms in a one-off timing, the members' picks the rest.

### Squads: Ambush (2026-10-28, phase S3)

Phase S3 of the owner's squad spec (section 4 "Ambush", setting the trap and springing it; the air ambush is S4): the kill zone,
the side picked, each role placed round it, garrisons, mines from the soldiers' own stock, Hold fire, the triggers, reload and
re-spring. Content: the `ambush` part of `content/mvp/squad.json` (optional; it needs `hold`: without it Ambush is ignored). Code:
`Sim/Systems/SquadAmbushSystem.cs` (in the squad pass; it reuses Hold's candidates and scoring in `SquadHoldSystem.cs`). Tests
`SquadAmbushTests` (16 tests, 26 cases); bench `SimRun -bench-ambush 50` (`bench/ambush_50.json`). Owner decisions (hard): only
Ambush lays mines, from each soldier's own stock; an ambush springs on its own; it uses garrisons as well as outdoor cover. Numbers
marked (spec) are the spec's; the rest are rows 653 to 672 of "Decisions to confirm".

- **The order.** `CommandKind.SetSquadMode` (48) with `Index` 3 (`SquadMode.Ambush`, appended), `Cell` the zone's start (where the
  enemy comes in) and `Cell2` its far end (where the column is stopped); no new order kind for it. A zone shorter than 6 cells or
  longer than 30 (spec), or none, is refused: `OrderRefusalReason.ZoneLength` (appended) in `World.OrderRefusals`, one per squad
  for its leader, the zone's start as the cell; the squad keeps its mode. Taken, the squad stops, goes to **Setting**
  (`SquadEventKind.Setting`) and every member to Hold fire. A Move or Attack-move ends the ambush (the squad takes March and walks;
  its garrisons come out, its mortar packs up, the mines it laid stay); so do another mode and Hold. Orders of their own (Attack,
  Guard, Stop, ...) keep it as in Hold: once done the members walk back to their positions; an Attack or Attack ground ordered from
  a set ambush springs it (the Order trigger).
- **The side (spec).** Once per order the squad scores both sides of the zone and takes the better one; everyone stays on it.
  A side's candidates are the free cells beyond the zone's width (2 cells either side of its line) within 12 cells of the zone's
  segment on that side (`radius`), the nearest the zone first, at most 600 (`cells`), reachable from the leader as in Hold, plus
  garrisonable buildings (below). Each member picks the best cell left for his role, in Hold's order (recon, long flank, flank,
  line, air guard, support, medic); the side's worth is the sum of the scores picked; the better side is taken, a tie the right
  (the right of start to far end). The right side is scored in one squad pass and the left in the next (one side a tick: about
  0.26 ms each). Several squads ambushing one zone take the side the first picked. The side's spot is 5 cells out from the
  zone's middle (`spot`): a squad farther than the radius from it marches there first (as March), then takes its positions.
- **Factors.** Hold's eight (cover, trees, sight, open, spacing, safety, range, near; design "Squads: Hold and position picking")
  with the zone as the threat: cover from the zone's point nearest the cell, sight lanes to the zone's middle, start and far end,
  range and nearness to the zone's segment, safety the depth back from it. And four of the ambush's: **end** (near the role's end
  of the zone along it: 100 there, 0 the radius away), **lane** (the share of the lane from the cell to the approach point seen,
  out to the soldier's own reach), **approach** (the approach point itself seen: 10 cells before the zone's start, `approach`),
  **hidden** (100 when none of the zone's three aims is seen, less a third for each seen).
- **Roles (spec; weights and limits content).** Weights [cover, trees, sight, open, spacing, safety, range, near, end, lane,
  approach, hidden]:

  | Role | Weights | Limits | Where it lands |
  | --- | --- | --- | --- |
  | Line (riflemen, engineers) | 5 1 4 0 3 0 2 1 0 0 0 0 | 3 to 8 cells from the zone's line (spec) | spread along the side, in cover |
  | Flank (RPGs) | 3 0 4 3 2 0 3 1 6 0 0 0 | 2 to 9 cells; ends in turn by ID | the two ends: the first the far end, the second the start (lead and tail) |
  | Long flank (Kornet) | 2 0 3 1 1 2 2 0 8 6 1 0 | not ahead of the line | toward the far end, on the longest lane down the road |
  | Recon (sniper) | 2 1 4 1 1 3 1 0 3 0 6 0 | 3 cells behind the line | farther back, toward the start, watching the approach |
  | Medic | 4 1 0 0 1 2 0 3 0 0 0 3 | 2 cells behind the line, out of sight of the zone | behind the line, hidden from the zone |
  | Air guard (Igla) | 1 8 0 0 3 1 0 1 0 0 0 0 | 8 cells from each other | under trees (the air corridor is S4) |
  | Support (mortar) | 3 0 0 0 1 3 4 0 0 0 0 2 | 8 to 12 cells from the zone's line (its minimum range) | back, in range; set up with the zone pre-aimed |

  The limits apply as in Hold: a soldier with no cell within them takes the best ignoring them (never another's cell); with none
  at all, where he stands.
- **Garrisons (spec).** A garrisonable building its side may enter, on the chosen side (its footprint cell nearest the zone beyond
  the zone's line), within the radius of the zone, with room, and a line of sight from its window height there to the zone's
  nearest point, is a position for roles that garrison (line, flank, recon: riflemen, RPGs, the sniper) whose reach plus the
  building's range bonus covers the zone from its edge; the medic, the Kornet team, the mortar and the Iglas stay out. Factors as
  in Hold (cover 100), with the ambush's from that edge. A member with mines lays them first, then goes in.
- **Mines (spec; owner: only Ambush lays them, from each soldier's own stock).** Once the side is known the squad plans the line's
  mines (`mines.layers`: the line role; the RPG's booby trap is not laid): **anti-tank mines** on the zone 2 cells apart along it
  (`atGap`) **from the far end back**, each on a road cell of the zone's width across its line where there is one (else on the
  line); **anti-personnel mines** 3 cells apart (`apGap`) on the cells 1 to 4 cells off the zone's line on its **far side**
  (`apBand`; off the road: where infantry run), the best cover from the squad's side first (Hold's cover points against fire from 5
  cells toward the squad), then the far end, then the cell index; the near side when the far side has too few; **booby traps**
  (one each, `trapsEach`: the engineer's) after them on the same cells. Each kind is dealt round the layers in ID order, one
  at a time, while their kits have charges, so the far end's anti-tank mines are laid first. Each layer lays his own one at a
  time (a `UseAbility` order: he walks there, 1.5 s to lay) and then takes his position; one with none left just takes his spot
  (spec). A mine laid is `SquadEventKind.MineLaid` (the layer, the mine) and in `Squad.AmbushMines`.
- **Hide (spec).** From the order until it springs every member holds fire (`Stance.HoldFire`, enforced each squad pass; a stance
  given meanwhile is the squad's and its members take it when it springs). Under trees that hides them from aircraft as the Hide
  stance does: concealment is the canopy's, and a soldier who does not fire is not exposed.
- **Ready (spec).** **Setting** until every member is at his position (on his cell and stopped, or in his building), his mines laid,
  the mortar set up; then **Ready** (`SquadEventKind.Ready`). After 60 s of Setting (`setTicks`: a member who can't get there) it is
  Ready anyway and the mines not laid are given up. The mortar sets up facing the zone's middle (pre-aimed).
- **Springing (spec).** Once placed and not marching, each squad pass, the first of: an enemy vehicle (`vehicles`: tag `vehicle`)
  in the zone within 3 cells of the far end along it (`farEnd`), **FarEnd**; 3 or more enemies in the zone (`crowd`), **Crowd**
  (ground units, no structures or traps, within the zone's width, spotted by the squad's side); a member spotted by an enemy
  (`Unit.SpottedBy`), **Spotted**. And, noted as they happen and sprung the next squad pass (also while it marches there): a trap
  going off in the zone (`Mine`), a member losing HP (`Hurt`), the player's Fire (`CommandKind.SpringAmbush` 49, appended; or an
  attack order), **Order**. Sprung: **Sprung** (`SquadEventKind.Sprung`, `Other` the `AmbushTrigger`; `Squad.AmbushTrigger`),
  mines not laid yet are given up and everyone takes the squad's stance.
- **Fighting (spec).** Each fights from his spot: nobody in an ambush chases (`CanChase` is false: no retaliation, no Aggressive
  hunt, no guard chase), his leash point his position. Target priority (above the existing keys): RPGs and the Kornet vehicles
  first, the line infantry first, the mortar what is in the zone first. The squad stays in Ambush; weapons reload as always.
  After 10 s with no member shooting or chasing, no attack order, no member hurt and no enemy in the zone (`rearmTicks`) it is
  **Ready** again (Hold fire) and can spring again; mines are not laid again unless the player sets the ambush again.
- **For the client.** `Squad.Mode` Ambush, `State` (Moving while it marches there, then Setting, Ready, Sprung), `AmbushStart`,
  `AmbushEnd` (the dashed strip), `AmbushSide` (1 right, -1 left, 0 not picked: the shaded side), `AmbushSpot`, `AmbushTrigger`,
  `AmbushTasks` (the planned mines: layer, cell, kind, state), `AmbushMines` (the IDs laid); `World.SquadAmbushMines(squad)` (those
  still on the map: cell and kind, the own-mine marks), `SquadHoldPick` / `SquadHoldGarrisoned` / `SquadSlotPoint` (each member's
  spot), `OrderRefusals` (`ZoneLength`); events Setting, Ready, Sprung, MineLaid (and Placed).
- **Determinism and cost.** Integer scores, ID and cell-index tie-breaks, no random draws, no hash collections. Hashed (once a squad
  has ambushed): the zone, the side and its spot, the phase, the last trigger, the pending one, the order's tick, the calm count,
  the mine tasks, the mines laid, the first side's picks and worth while the second is scored. Candidates and factors are derived.
  50 squads of 10 on the Hold bench's map each setting an ambush on a 20-cell zone next to them, then a technical driving down each
  zone (`SimRun -bench-ambush 50`, Release, M3 Pro): the squad pass 0.48 ms of tick p99 (max 1.30), the path system +0.12 ms, gate
  0.60 ms < 1 ms: met; one order's picking (both sides, 724 cells) 0.51 ms, spread over two ticks.

### Squads: Air ambush (2026-10-28, phase S4)

Phase S4 of the owner's squad spec (section 4 "Air ambush" and the S4 line of section 7): a squad whose members are mostly Iglas
sets its ambush round an **air corridor**, the line the player expects aircraft to fly. The Iglas spread along it under trees, never
closer than 8 cells to each other (owner, hard: an all-Igla or mostly-Igla squad forms up well and **never bunches**, so one bomb
kills one); the rest of the squad guards them from the ground; it springs when an aircraft comes within reach of 2 Iglas, a member
is hurt or the player fires, and the Iglas **fire together**. Content: the `air` part of the `ambush` block in
`content/mvp/squad.json` (optional: without it every ambush is a ground one). Code: `Sim/Systems/SquadAirAmbushSystem.cs` (the
picking, the triggers and the volley), hooks in `SquadAmbushSystem.cs` (the order, the squad pass) and `AirDefenseSystem.cs` (the
volley). Tests `SquadAirAmbushTests` (12 tests, 22 cases); bench `SimRun -bench-air-ambush 50` (`bench/air_ambush_50.json`).
Numbers marked (spec) are the spec's; the rest are rows 673 to 690 of "Decisions to confirm".

- **The order.** The same Ambush order (`SetSquadMode` 3, `Cell` and `Cell2`); the squad's make-up chooses the kind: more than
  half air guards (`mostly` 50: the Igla's role, tag `manpads`) is an air ambush (`Squad.AmbushAir`), else a ground one (half is
  not mostly). `Cell` is the corridor's start (where aircraft come from), `Cell2` its far end. A corridor shorter than 20 cells or
  longer than 120 (`length`) is refused with `OrderRefusalReason.ZoneLength`, as a ground zone outside 6 to 30 is.
  `World.AirAmbushSquad(squad)` tells the client which kind an order would set (which limits to draw). Moves, modes and Hold end it
  as they end a ground ambush.
- **The Iglas (spec).** Their slot points lie on the corridor's line, centred on its middle, the corridor's length shared out
  between them but at least 8 cells (`gap`) and at most 15 (`gapMax`) apart: 6 Iglas on 80 cells stand 15 apart, 10 on 20 cells 8
  apart (past the corridor's ends). Each, in ID order along the corridor, takes the best cell within 4 cells of his slot
  (`search`) for the air guard's weights [cover, trees, spacing, near] = 0 8 3 2: under canopy that hides from the air
  (`concealment`'s densities: grove or dense forest) first, away from the Iglas placed before him, near his slot. Hard limits: 8
  cells from every Igla already placed, his squad's or another of the player's holding or ambushing squads' (two squads on one
  corridor never bunch either), 1.5 cells from anyone. None left: 8 cells round his slot, then (past the squad's 600-cell cap)
  16 and 32: he never gives up the 8 cells. Within 32 cells of the corridor's line, every Igla has it within his 140-cell reach.
- **The guards (spec: riflemen guard the Iglas from ground attack).** Everyone who is not an air guard (riflemen, engineers, RPGs,
  a sniper, ...) guards. The likely ground approach is 10 cells before the corridor's start (`approach`: where the aircraft come
  from, their side). The guards are dealt round the Iglas nearest the approach first, in ID order; each stands 2 to 5 cells from
  his Igla (`guard`) and nearer the approach than it, the best cell within 4 cells of the point 3.5 cells from the Igla toward the
  approach for the guards' weights 5 2 2 4 (cover from the approach, canopy, spacing, near). A medic stands behind his Igla
  instead (farther from the approach). None left: 8 cells round, then without the guard limits.
- **No mines, no garrisons.** An air ambush plans no mines (the corridor is no road) and nobody goes into a building.
- **Hidden until sprung (spec).** Everyone holds fire from the order until it springs, as in a ground ambush: under the canopy a
  soldier who does not fire is hidden from aircraft (the concealment rules), and an Igla holding fire neither tracks nor locks
  (nothing gives him away). Setting, then Ready once everyone is in place (`setTicks` at most).
- **Springing (spec).** Once placed, each squad pass: an enemy aircraft (a flying unit with a flight block, not on the ground)
  within reach of 2 of its Iglas (`trigger`) springs it, **Air** (`AmbushTrigger.Air`, appended). An Igla reaches an aircraft
  when he has a missile, his interceptor engages its class, it flies under his ceiling, the slant from his eye is within his
  weapon's 140 cells and not inside its 8-cell minimum, and he sees it himself (his 150-cell air vision times its signature: the
  fighter only from 75). A member hurt (**Hurt**) and Fire (**Order**) spring it as in a ground ambush. The ground triggers (far
  end, crowd, a mine, being spotted) do not apply: a ground enemy who finds the guards is fought once one of them is hurt.
- **The coordinated volley (spec: they fire together so flares cannot beat them all).** Sprung, the squad keeps one target
  (`Squad.VolleyTarget`): the enemy aircraft most of its Iglas reach (ties to the lowest ID), kept while one of them reaches it.
  Every Igla that can engage it (the air defence's own rules: Guard, idle, his sight or his side's tracks within his reach) turns
  to it and locks on (1.5 s); one loaded, in reach and with no missile of his side already at it takes part. The locked ones hold
  their missiles until every one taking part is locked, or 1 s (`volleyWait`) after the first was; then they all launch in the
  same tick, after every air defence unit has run (`SquadEventKind.Volley`: the aircraft, the missiles). The side's usual "one
  missile at a target at a time" is not broken: the volley is that one moment; the next waits for the missiles in flight and
  the reload. An Igla who cannot engage the volley target engages others as always.
- **What the flares see.** The flare logic is unchanged and per missile: each hostile missile homing on the aircraft gets its own
  salvo within the window (while the dispenser has salvos), and a fixed wing breaks against one missile at a time, dispensing at
  once with the better decoy (`breakPk` 30% left instead of 55%) and turning to beam it. A volley puts several missiles in the air
  at once, so the break and its decoy cover one of them; the others meet a plain salvo (55%) or none, and the jet's 8 flares run
  out sooner. Measured (`TheIglasFireTogetherAndFlaresBeatSinglesMoreOften`: 4 Iglas, a multirole flying down the corridor, 12
  seeds and lateral offsets, the same Iglas at the same cells loose in Guard for the singles): singles 96 missiles, 33 fused by
  the jet (the break makes the rest overshoot), flares left them 30.0% of their chance, 1.4% kill chance a missile, 1 hit, the jet
  down in 0 of 12 runs, the first single shot 0.0%; volleys 153 missiles, 134 fused, flares left 65.6%, 10.3% a missile, 15 hits,
  the jet down in 6 of 12, the first volley 23.9% to hit.
- **Sprung and after.** As a ground ambush: the squad's stance, nobody chases, Ready again after 10 s calm: no member's ground or
  air target, chase, attack order or HP lost, and (an air ambush) no enemy aircraft in reach of an Igla. A sprung ambush springs
  again the same way.
- **For the client.** `Squad.AmbushAir` (draw the corridor, not a kill zone; `AmbushSide` stays 0, `AmbushTasks` empty),
  `AmbushStart` / `AmbushEnd` (the corridor), `AmbushSpot` (its middle), `VolleyTarget`, `AmbushTrigger` (`Air`); each member's
  spot as in S3 (`SquadHoldPick`, `SquadSlotPoint`); `World.AirAmbushSquad(squad)`; `SquadEventKind.Volley` (appended);
  `OrderRefusals` (`ZoneLength` with the corridor's limits).
- **Determinism and cost.** Integer scores, ID and cell-index tie-breaks, no random draws, no hash collections. Hashed (once a squad
  has set an air ambush): the kind, the volley target and the tick the first Igla was ready (inside S3's ambush state). The
  aircraft list is scratch built once a tick when an air ambush needs it. An air ambush's picking costs a squad pass's whole
  picking budget (one a tick). 50 squads of 10 (6 Iglas, 4 riflemen) on the Hold bench's map each setting an air ambush on a 36-cell
  corridor beside it, then jets flying over every second row (`SimRun -bench-air-ambush 50`, Release, M3 Pro, against the same
  soldiers walking to the same cells on plain moves, their Iglas firing on their own): the squad pass 0.33 ms of tick p99 (max
  0.36), the air defence +0.25 ms, the path system +0.04 ms; gate 0.61 ms < 1 ms: met; 50 sprung, 54 volleys. One order's picking
  0.15 ms (max 0.46 ms): about 1,300 cells, since neighbouring squads' Igla lines meet and the end Iglas look farther out
  (a 4-cell search is 49 cells a member).

### Squads: playtest fixes (2026-10-28, SQ-FIX)

The client's first playtest of Hold and Ambush found three picks that looked wrong. Each fix is a rule or a content weight, with a
regression test in `SquadFixTests` (each test's map fails before its fix). Rows 691 to 695 of "Decisions to confirm".

- **Support on slopes.** The picking never checked the ground's grade, so on one layout the mortar took a cell on a 30% slope
  and never set up (the deploy code refuses it). Now a member who must set up before firing (`deployTicks`: the mortar) never
  takes a cell the deploy code would refuse (`CanDeployAt`: under his movement class's `deployMaxGrade`, the foot class's steep
  threshold of 25% by default, and his own `deploySlope`), in Hold, Ambush and the air ambush, in every pass (it is the ground,
  not a preference). With every cell 10 cells behind the line sloped, he takes the best flat cell of the relaxed pass. Test map:
  the ground behind the spot rises 30%; before, the mortar picked (32, 44) at 29% and stayed packed; now (23, 39) at 0% and sets up.
- **Recon on hilltops.** The recon's Hold weights were [cover, trees, sight, open, spacing, safety, range, near] = 2 0 6 1 1 3 2
  0: cover, safety and his rifle's range to the threat outweighed the sight a hill gives. Now 1 0 8 1 1 1 0 0: sight leads, his
  own range does not count (he spots; he fires only at threats to the squad), safety and cover break ties. Test map: a ridge
  ahead, a clear hill (5 m, 40% sides) 10 cells right and 5 back, a dense wood behind the spot; before, the marksman took the wood
  (31, 37) on flat ground; now the hill's front slope (40, 33), 2.2 m up, sight 80 or more. The ambush's recon weights are unchanged (the
  approach point leads there).
- **The line and buildings away from the spot.** The line's Hold nearness weight was 5, so a building a few cells from the spot
  lost to any covered cell beside it. Now 4: a garrisonable building within the riflemen's reach of the threat and with sight of it
  is taken over a wall cell by the spot that sees little. Lower (3 or 2) sends riflemen to woods 9 cells behind the spot on
  `SquadHoldTests`' crafted map, so 4 is the floor that keeps the line on the wall there. Test map: a low wall and dense wood at
  the spot, a small house 6 cells right and 7 ahead; before, 2 of 4 riflemen took the house; now 3 (the fourth keeps the best
  wall cell). `TheLeashPointIsTheHoldCell`'s enemy is now a rifleman: with the line farther forward a marksman 10 cells behind
  the spot shot the riflemen one at a time as they came back.
- **The tail's RPG died first** (the client's `--demo=ambush-road`, both column runs). An ambush deals its RPGs to the two ends
  (the lead vehicle's far end first, then the tail's end at the zone's start), so the second one stood 3 to 4 cells off the road
  where the column comes in: the first soldier it met. Now the RPG at the start end keeps to the far half of his band off the
  zone's line (the flank's `lateral` 2 to 9 cells: 5.5 or more), deeper in his cover, still within his launcher's 9 cells of the
  road; the far-end RPG is unchanged. Test map (`SquadAmbushTests`' column run): before, he took (16, 27) and died first; now
  (19, 23), inside the woods, and a line soldier is the first hit; the column of three is destroyed either way.
- **Which trigger fires first (owner's call: as designed).** In the column runs the far-end trigger almost never fires first:
  the AT mine laid 2 cells short of the far end goes off when the lead vehicle is 3.2 cells from it, before the 3-cell far-end
  distance, and the line soldiers 3 to 8 cells off the road are inside a technical's 8-cell sight, so **Spotted** (or **Mine**)
  usually springs the trap. That is realistic: the mines are there to stop the column, and a spotted trap must fire at once.
  The trigger distances and the line's distance from the road are unchanged.

### Squads: the skirmish AI (2026-10-28, phase S5)

Phase S5 of the owner's squad spec (the owner: the computer opponent uses squads and ambushes too). `SkirmishAi` forms squads of
its infantry and gives each a job; it issues only the player's commands (`FormSquad`, `SetSquadMode`, `AttackMove`, `Attack`,
`SetStance`, `Disband`), so replays never need it. Code `Bots/SkirmishSquads.cs` (with hooks in `SkirmishAi.cs` and one in
`SkirmishScouting.cs`); tests `SkirmishSquadTests` (8 tests, 10 cases); `SimRun -skirmish TICKS` plays it against itself. Rows 696
to 706 of "Decisions to confirm". `UseSquads = false` plays exactly as before S5 (`SkirmishAiTests`' guard-post and wave tests use
it).

- **Forming.** Each decision (every second) the free infantry at home that may join a squad (not workers or pilots, not out with a
  wave) are formed up. First, with 3 Iglas or more at home (`AirIglas`) and no air ambush squad, one squad of up to 6 Iglas and
  fewer line soldiers (mostly Iglas: an air ambush). Then, with 4 or more left (`SquadMin`), as few squads of at most 8
  (`SquadSize`) as hold them all, the soldiers dealt round them sorted by role then ID, so each squad gets a share of every class
  (15 soldiers: 8 and 7, each with riflemen, an RPG and more). Iglas left over stay loose on Guard, the base's air defence.
- **The enemy's way and the territory.** The way in is toward the remembered enemy structure nearest the base, else the point
  opposite the base across the map's centre (the side's own knowledge only, as the waves'). The territory is every cell within
  30 cells (`TerritoryCells`) of the base's centre or of a structure of its own (`SkirmishAi.InTerritory`). Every ambush and hold it
  orders lies inside it.
- **Jobs, in squad ID order.** An Igla-heavy squad sets an **air ambush** on the corridor from 26 cells out toward the enemy in to
  4 cells from the base (shortened to stay in the territory; none under the rules' 20 cells: it waves instead); its stance Guard.
  A ground squad gets a **road ambush** while fewer than 1 (`AmbushSquads`) is set, else the **hold** of the key spot (10 cells
  toward the enemy, `HoldAt`, facing that way) while fewer than 1 (`HoldSquads`) holds, but only while at most half its ground
  squads have a job at home; the rest are **wave squads** (Aggressive). A wave squad back home with nothing to do takes over an
  ambush or the hold that lost its squad.
- **Picking the road ambush.** Every road cell in the territory on the enemy's side of the base starts a straight run of road
  cells toward the base in each of the 8 directions, up to 16 cells (`AmbushCells`; the engine's 6 to 30). Score (integers): how
  well it points from the enemy to the base (x2, -1000 to 1000), its length (20 a cell), its middle near 16 cells from the base
  (`AmbushAt`, -30 a cell off), and woods (grove or denser) 3 and 5 cells either side of it (5 a cell sampled, every second cell
  along); none within 10 cells of another squad's zone; ties to the first in row order, then direction order. Its start (where
  the enemy comes in) is the far end from the base. Without terrain or a road there is no ground ambush. The engine then picks
  the side, the positions and the mines, and springs it on its own.
- **The waves.** Wave squads go out with the normal timed waves: March (a file; the squad's path takes the roads), attack-moving
  with the loose fighters. A wave squad turns to Assault once a spotted ground enemy is within 20 cells (`AssaultCells`) and back
  to March when none is within 30. Out in the field and idle it attacks a passive structure it can hit, else attack-moves to a
  spotted enemy within 30 cells, a remembered structure, or the unseen probe nearest itself (not the base: one squad sweeps in
  short hops; the loose fighters' choice is unchanged), else home. Wave squads at home join the defence against an intruder.
- **Endgame.** When a wave finds nothing else left to fight (no loose fighter and no wave squad, at home or out), the ambush and
  hold squads go with it and become wave squads, the air ambush squad disbands (its guards go as loose fighters, its Iglas stay
  home), its air defence and launchers go with the waves too, and it gives no more jobs at home. Without this the AI kept most of
  its infantry home and matches stalled: on `SimRun -skirmish` 7 of 9 seeds ended in 20 minutes (squads off: 6 of 9).
- **Determinism and cost.** No random draws at all (the world's `Rand` is not touched): integer scores, cells in row order, squads
  and soldiers in ID order. The AI's think time (`SimRun -skirmish 24000`, Release, M3 Pro, the wall clock round `Think`, both
  AIs; the two runs are different matches, 20 minutes without squads and 6:21 with them): 0.001 ms a decision on average without
  squads, 0.013 ms with them (p99 0.014 and 0.018 ms); the decision that gives new
  squads their jobs (the road zone search over the territory's 61 x 61 cells) 0.046 ms warm (2.7 ms the first time, cold JIT).
- **A match** (`SimRun -skirmish 24000`, seed 42: the battle lobby plus 4 Iglas and a mortar a side, a 128 x 96 map with a highway
  along the middle, two crossing roads and woods): each side formed four squads (7, 7, 7 and 6); one set an air ambush along the
  highway's corridor, one a road ambush on the highway, 16 cells long ({31, 47} to {15, 47} for player 1), its far end about 10 cells from the base; the two others marched
  out with the first wave and turned to Assault on contact; the ambushes sprang on Spotted and Hurt as the waves reached the bases;
  at 3:00 player 1, with nothing left out, sent its ambush squad and launchers in (endgame); player 2 won at 6:21.

### Squads: slots by place (2026-10-30, SQ-ORDER)

The owner's note of 2026-10-28: on a March order a member whose role slot is at the rear but who stood at the front walked
**back** to his slot, and the squad waited for him, so the whole squad was slow. Now nobody walks against the move and the squad
never waits for a member who is only "behind his slot" because he is ahead of it. Code `Sim/Systems/SquadSystem.cs` ("Slots by
place"); tests `SquadOrderTests` (7 cases). Rows 708 to 717 of "Decisions to confirm".

- **Slots by place on a new order.** When the squad's path for a new order is planned, the shape faces the goal (straight) and
  every member's place is noted: how far ahead of the anchor along that way and how far to its right, in half cells to the
  nearest (`Squad.Places`). The slots laid out by role (as in S1) are then handed out by place: front to back, the members most
  ahead take the forward slots; slots at one depth (the Assault line) go right to left to the members from the right. Ties go to
  role order (the slot the role would have), then ID. The depths and gaps stay the role layout's: members swap slots.
- **Roles kept sensible by the swap.** The recon slots go to the recon. The head (the anchor's slot: the front of the March file,
  the middle of the Assault line) goes only to a soldier who may lead (not a medic, recon or support: the `noLead` roles), so the
  foremost of those is the head on the move; the leader (the lowest-ID one who may lead) is unchanged and is the head again at
  rest. The rest of the Assault line goes only to soldiers whose role stands in it (line and flank): a medic or mortar never
  walks in the line, the rear roles swap among the slots behind it.
- **Role order at rest.** The places hold until the order ends (arrival, a new order, an order of the members' own); the slots
  then go by role again, and on arrival the members walk to their role slots at rest. A new mode on the move (March to Assault)
  notes the places again; a soldier who joins on the move ranks last.
- **Never walking back.** While the squad moves, a member whose slot is behind him along the way that slot moves (the trail there,
  for a March slot; the facing otherwise) does not walk to it: he goes sideways to the point level with him on the slot's line
  (straight, when that is clear; else he stands) and the slot comes to him. He does not count for the regroup wait, which now
  holds the squad only for a member more than 4 cells **behind** his slot.
- **Starting at the head.** After the hand-out the anchor starts as far along its path as the least that any following member
  stands ahead of his slot (from half a cell): a file sent back the way it came turns about where it stands, its rear man the new
  head, instead of standing while the anchor walks through it. If the old trail lies ahead of the anchor (a reversed order) the
  file starts straight back from the anchor rather than folding back along the old footsteps; a turn of 90 degrees still bends
  round the corner (row 602).
- **Measured** (`SquadOrderTests`, the open 128-cell arena, a leader, two riflemen, a tank hunter and the mortar (the slowest,
  1.3 cells/s) 3 cells ahead of the leader, 70 cells east): March arrived in 1,087 ticks, its anchor's straight walk at the
  mortar's speed 1,087, no tick waited (before: 1,147, 60 ticks waited, a rifleman's first step 97 raw back); Assault 1,379 =
  1,379 at 80% (before: 1,439, 60 waited, the mortar's first step 66 raw back). No member wished a step back; the most a
  neighbour nudged one back was 32 raw (1/32 cell) in a tick. An about-turn (the file walking west, sent east) arrived in 1,328
  ticks against the anchor's 1,397 from the leader (it starts about 4.5 cells on, at the new head), no tick waited.
- **Determinism and cost.** Integer projections, ID and role order for ties, no random draws; `Squad.Places` is hashed (only while
  ranked: squads at rest and games without squads hash as before; the golden battle forms no squad). `SimRun -bench-squads 50`
  (Release, M3 Pro): the squad pass p99 0.141 ms, mean 0.115 (a981dd0: 0.109 and 0.093); the gate of 1 ms met.

### Traps (2026-10-18)

The assault soldier lays two mines (kits `ap_mine_kit` and `at_mine_kit`: 1.5 s to lay one, up to
1.5 cells away; 4 and 2 charges, a depot refills them; at most 8 and 4 of one soldier's on the
map, laying another lifts his oldest). Numbers are in `content/mvp` (`ap_mine_charge`,
`at_mine_charge`, warheads `ap_blast` and `at_blast`); the code is `Sim/Systems/TriggerSystem.cs`.

| | Anti-personnel mine (`ap_mine`, PMN-2 type) | Anti-tank mine (`at_mine`, TM-62 type) |
| --- | --- | --- |
| Set off by (contact) | any enemy ground unit within 0.6 cell (1.2 m) | an enemy vehicle within 1.2 cells (2.4 m: under its hull); soldiers walk over it |
| Blast | 3 cells (6 m), 120 at the centre falling to 30 at the edge | 1.5 cells (3 m), 80 falling to 40 |
| Against soldiers | killed within about 2 cells, wounded out to 3 | hurt or killed close by |
| Against vehicles | a scratch (10% light and medium, 2% armoured) | the vehicle that set it off is destroyed outright, whatever its armour; no other vehicle is ever killed by it (left at 1 HP at worst) |

- **Trigger zone smaller than the blast** for both, so a soldier next to a mine is safe from
  setting it off but not from its blast.
- **On demand.** Its owner can set off any of his traps at once (`Detonate`). An anti-tank mine set
  off on demand destroys the nearest enemy vehicle within its trigger zone, if any.
- **Auto off.** Its owner can turn a trap's going off on its own off and on again, on the trap itself
  (`SetAutoDetonate`; the soldier who laid it has no such switch). A trap set to on-demand ignores
  enemies stepping on it, but still goes off on demand and in a chain.
- **Chains and stacking.** A trap that goes off lights every other trap (either side's) whose centre
  lies within its blast radius; a lit trap goes off `chainTicks` later (2 ticks, 0.1 s), and lights
  the traps in its own blast in turn. So traps laid within each other's blast form one bigger trap:
  stepping into any mine's trigger zone sets off the whole field link by link, the blast covers the
  union of their blasts, and damage adds up where blasts overlap (a soldier 2.2 cells from two mines
  survives one, not both). A trap blast never hurts traps: they chain instead. An anti-tank mine's
  small blast (1.5 cells) chains only what lies right next to it; two chained anti-tank mines can
  destroy two vehicles, one each.
- **Order within a tick.** Lit fuses count down first (all of them, then the due traps go off in ID
  order, so a fuse lit this tick is counted from the next), then contact checks in ID order.
- **Detection.** A detector reveals a hidden enemy within its `detect` radius for good: the sniper
  5 cells, the scout drone 25 cells (around the point under it), the observation post 10 cells.
  With fog of war the trap's cell must also be in sight of the detector's side that tick, so a ridge
  hides a mine from a sniper but not from the drone above.

### Launchers aim with the hull (2026-10-28, L-YAW-E)

The owner's rule: every truck-mounted launcher box stays in line with the truck's heading and only elevates; **the truck turns
to aim**. The game client draws the box locked to the hull (L-YAW). Before this change the engine still aimed these units with
a turret (`turret: true`): `Unit.TurretFacing` turned to the target at 2 facings a tick and the hull kept its heading, so a
salvo could leave a locked box pointing away from its target. Decisions 621 to 632.

- **Two archetype flags, opt-in data** (content `archetypes/vehicles.json`; `Archetype.HullAims`, `HullArc`, `VerticalLaunch`;
  the content hash covers them):
  - `hullAims` (needs a `deployTicks` and no `turret`): a box fixed on the hull. Stowed, with an attack or attack-ground order
    that puts it in a firing position, the truck **turns in place** to face what it fires at (at its `turnRate`, facings a
    tick) and **sets up only once it faces it**. Set up, it never turns (jacks down) and fires at what lies within its arc;
    when its target is **off the arc between salvos** (a new order, the next Shift-queued point, a unit target that drove
    round) it **packs up, turns and sets up again**. A salvo in progress is never broken off for the arc (its rounds fly to
    the point fixed when it began).
  - `hullArc` (degrees either side, with `hullAims`; rounded down to whole facings of 11.25 degrees, at least one; default one
    facing, the fire alignment every weapon already had): the arc. 180 (any way) for a missile that turns on its own after
    leaving the rail: the cruise truck's missiles (each on its own route) and the drone truck's drones (one a point).
  - `verticalLaunch` (no `turret`): fires straight up and lets the missile turn; the unit **never turns to aim**, hull or box.
- **Per unit** (what changed in `content/mvp`; the client should check that its drawn box stays on the launch line):

  | Unit | id | Now | Arc |
  | --- | --- | --- | --- |
  | 30-tube rocket truck | `heavy_rocket_launcher` | hull aims, turns before set-up | 1 facing |
  | Heavy rocket truck | `heavy_rocket_truck` | hull aims | 1 facing |
  | Long-range rocket truck | `longrange_rocket_truck` | hull aims | 1 facing |
  | Rocket jeep | `rocket_jeep` | hull aims | 1 facing |
  | Twin missile truck | `missile_truck` | hull aims | 1 facing |
  | Tactical (ballistic) missile truck | `ballistic_truck` | hull aims | 1 facing |
  | Kaman launcher truck | `kaman_truck` | hull aims | 1 facing |
  | Drone launcher truck | `drone_truck` | hull aims (faces its first point) | any way (180) |
  | Cruise missile truck | `cruise_truck` | hull aims (faces its first point) | any way (180) |
  | S-400 type launcher | `s400_launcher` | vertical: never turns | - |
  | Hypersonic missile truck | `hypersonic_truck` | vertical: never turns | - |
  | Air defence vehicle | `sam_vehicle` | no turret: the hull turns to its air target (2 facings a tick, the old turret's rate), no set-up | - |
  | Patriot and THAAD types | `patriot_launcher`, `thaad_launcher` | unchanged: no turret already; radar-linked, they fire from their fixed box and the missile turns | - |
  | Light and medium rocket trucks | `light_rocket_truck`, `medium_rocket_launcher` | unchanged: no turret, no set-up; the hull turns at its turn rate before each salvo (the fixed-weapon rule) | - |
  | SA-6 type launcher | `kub_launcher` | unchanged: its turntable (`turret`), not a truck box | - |

- **Time to the first round** (ticks from the order to the first charge spent; `HullAimTests.TheHullFacesThePointWhenItFires`;
  before = the old turret, after = the hull), the point straight ahead / 90 degrees off / 180 degrees off a stowed truck, and
  90 degrees off one already set up facing the other way:

  | Unit | Ahead | 90 degrees | 180 degrees | Set up, 90 degrees off |
  | --- | --- | --- | --- | --- |
  | `heavy_rocket_launcher` | 51 / 51 | 54 / 58 | 58 / 66 | 4 / 99 |
  | `heavy_rocket_truck` | 81 / 81 | 84 / 88 | 88 / 96 | 4 / 149 |
  | `longrange_rocket_truck` | 121 / 121 | 124 / 128 | 128 / 136 | 4 / 229 |
  | `rocket_jeep` | 61 / 61 | 64 / 64 | 68 / 68 | 4 / 105 |
  | `missile_truck` | 121 / 121 | 124 / 128 | 128 / 136 | 4 / 229 |
  | `ballistic_truck` | 201 / 201 | 204 / 208 | 208 / 216 | 4 / 329 |
  | `kaman_truck` | 41 / 41 | 44 / 44 | 48 / 48 | 4 / 85 |
  | `drone_truck` | 41 / 41 | 44 / 44 | 48 / 48 | 4 / 1 |
  | `cruise_truck` | 161 / 161 | 164 / 168 | 168 / 176 | 4 / 1 |
  | `hypersonic_truck` | 201 / 201 | 204 / 201 | 208 / 201 | 4 / 1 |

  A stowed truck pays its turn (a half turn: 16 ticks at turn rate 1, 8 at 2) instead of the turret's (8 ticks), before the
  set-up instead of after it: at most 8 ticks (0.4 s) more. A set-up truck re-aimed off its arc pays its stow, turn and set-up
  (4 to 16 s) where the turret took 4 ticks: the real cost of the rule, and the lever (decision 624). The air defence vehicle
  fires on tick 159 of the `SamVehicleTests` engagement whichever way it faced, as before; the S-400 type launcher 42 ticks
  after the jet's arrival facing any way (42 east, 45 north with the turret).
- **Air defence.** The air defence vehicle's hull turns at 2 facings a tick, the turret's rate, and it engages only standing
  (as before), so it loses nothing. The Patriot and THAAD types and the S-400 type launcher never turn. No set-up air defence
  unit turns its hull with its outriggers down.
- **For renderers.** For every unit above but the SA-6 type, `Unit.TurretFacing` equals `Unit.Facing` at every tick (the
  box along the hull); a hull-aiming truck turns in place (no movement) before `Deploy` goes `Deploying`, and when re-aimed it
  goes `Stowing`, turns, then `Deploying` again. The vertical launchers' `Facing` stays as it was parked.
- **Hashing.** No new state: `Facing`, `TurretFacing` and the deploy state were hashed already. The new archetype fields are
  in the content hash.
- **Tests.** `HullAimTests` (content, refusals, per class the hull on the line when it fires and never turning set up, the
  arc, re-aiming a set-up truck at a unit, the air defence vehicle, the S-400 type launcher, determinism). `SamVehicleTests`
  now expects the hull to turn; `PatriotTests`' firing truck faces the battery.

### Multi-target ground attack (2026-10-30, MT)

The owner's spec of 2026-10-29 (the game repo's `rts-work/multi-target-spec.md`, answers 1 to 10): a command like Ground attack
where the player clicks several targets in turn; the whole selection takes them as **one objective** and coordinates so that
every point is hit. Ground attack and Attack stay as they are. Code `Sim/Systems/MultiTargetSystem.cs` and `Sim/MultiTarget.cs`;
tests `MultiTargetTests` (20). Rows 718 to 735 of "Decisions to confirm".

- **The order.** `CommandKind.MultiTarget` (50) with `Command.Aims` (`MultiAim`: a cell and an enemy unit or 0), at most 64
  (`World.MaxMultiAims`). Points off the map, a point or unit twice, the player's own units, untargetable pieces and aircraft in
  the air are dropped. Its units that can take one of the points (a weapon for the ground; a parked jet whose full load has one
  rearms first, D-ORD2) become the members; an aircraft that cannot says why (`World.OrderRefusals`). The objective
  (`World.MultiTargets`, `World.MultiTargetOf`) holds the points (`MultiPoint`: shots, units on it, first hit tick, unit gone)
  and the members (`MultiMember`: the point each is on).
- **A shot** is what a unit puts on one point (answer 4): a ground or helicopter weapon's one round, or its whole salvo when the
  weapon's `volleyPerTarget` is set: the ripple launchers (rocket trucks, the MLRS, the jeep's pair, the helicopters' rocket
  pods; the default for any salvo). A fixed wing's shot is a pass that released: its fire mode's group of bombs (answer 9 and
  the bombers' decision: the points are salvo aims, one group a point; a bomber in a spread mode, carpet20 or block24, takes its
  first mode that is not spread, salvo4 or row4, while on the objective and gets its own back when it leaves), or a strafing
  pass once the bombs are gone. With `volleyPerTarget: false` on a salvo weapon each round counts for its point and the rest of
  the salvo aims at the unit's next point while that is in reach, outside the minimum range and within a hull-aimed launcher's
  arc (else it stays on its point).
- **The assignment** (answers 2, 3 and 5). A free member takes a point with the fewest shots and units on it (every point is
  covered before any gets a second; then the spare shots spread evenly); among those first the point the fewest free units can
  take (a point only an immobile gun reaches is not left uncovered by a greedy pick), then the nearest unit and point together
  (straight line), ties by unit ID, then point order. At the order every member is free; later a member is free once its shot
  is delivered, its aimed-at unit is gone, or it gave its point up (lost, refused: never given that point again). A member that
  can take no point (dry, every point it could fire at gone) leaves; a dead member or one that cannot act leaves and its point
  goes to the next unit that is free. Units go on spreading their spare shots until they run dry, as Ground attack fires until
  dry (answer 1: a jet with 4 bombs and 2 points drops 2 on each).
- **Which points a unit can take.** A ground point: a weapon for the ground and, from where it stands, the point not inside its
  minimum range (Attack-ground closes in but never backs off); a unit that cannot move (the howitzer) only points in its range.
  An enemy unit: alive, not inside something, a weapon that hurts it; a ground unit or a helicopter only while its side sees it,
  an aircraft also unseen (it flies to the last known cell, E13). Gunships take no part (they fire at what they see round their
  circle, never at a point).
- **Movement and fire** (answer 7). Each point is given as the member's own Attack-ground (or Attack) order through the command
  path (squad members alone, not their squads), so it walks or drives into range, sets up and fires, flies its attack run or its
  bomber pass, refuses as that order would. A fixed wing already on a strike is retargeted at its break (`Retarget`, H2): it flies
  its break and extend and comes round onto the next point. A unit still firing a salvo of an earlier order finishes it at the
  old aim; its own shots count from then.
- **Hits.** A point counts as hit when its shot is released or fired, not on impact (an intercepted rocket still counts). A point
  hit only by another point's splash is not hit: every point gets its own shot. An aimed-at unit destroyed by the volley fired at
  it (before the volley is over) is hit; one destroyed before its shot began is done (`Gone`). The objective is complete (`CompleteTick`) once every point is done; the client's "points hit x / y" is
  `MultiTarget.Done` over `Points.Count`.
- **Leaving.** Any other command to a member takes it off (settings, production and sandbox commands do not); an order from
  elsewhere (a squad's) does too. Objectives without members go.
- **The client's preview.** `World.PreviewMultiTarget(player, units, aims)`: the starting assignment as (point, unit) pairs, pure;
  the lines drawn while the points are placed.
- **Measured** (`MultiTargetTests`, Debug test build on this machine): 50 mortars on 50 points, the order's commands phase 1.04 ms
  against 0.52 ms for the same 50 units given one Attack-ground each (its tick 50.1 ms against 49.5 ms, both the first routes);
  the ticks after, the whole world, median 0.38 ms, p99 1.09 ms. A multirole jet with 4 x 500 lb bombs on 4 points 15 to 30 cells
  apart: one bomb each, the last released at tick 1,029 of the order (a 320-cell map, 160 cells out).
- **Determinism.** Integer distances, ID and point order for ties, no random draws. The objectives are hashed (`World.Hash` and
  `HashReference`) only once one was made: the golden battle gives none and is unchanged; `volleyPerTarget` is in the content
  hash only off its default, so the content stating it hashes as before.
- **Not in this job.** The skirmish AI does not use it (answer 10). A bomber flies a pass per point: close points are not bombed
  together in one pass (the aims queue). Simultaneous impact (answer 6: not wanted now).

## Resistance faction

The Resistance wins on numbers, cheap drones, fast light vehicles, a rocket fleet that has to be resupplied, and tunnels. It has no planes. A faction is a set of art and preset rules on the same engine; the second faction, the Sentinel (below), came as data plus one new behavior (the watcher system).

**Infantry.** Trained one soldier at a time. Each soldier carries two things at once, a primary and a secondary. Before the match a player saves up to 3 presets per class; each preset is a pair of picks from that class's list.

| Class | Role | Loadout (MVP) |
| --- | --- | --- |
| Assault | Light frontline, lays mines | Primary: rifle, SMG or LMG; secondary to be decided; always both mine kits (anti-personnel and anti-tank) |
| Engineer | Utility | Kit: booby traps, FPV drones (2) or repair; no anti-armor role for now |
| Anti-armor | Kills vehicles and drones | Pairs such as AT missile + booby trap, or AA missile + short-range rifle |
| Sniper | Long range, sees far, reveals traps (5 cells) | Sniper rifle + short-range gun |
| Mortar | Indirect fire, 8 to 30 cells, over ridges and woods | One light mortar (fixed, no loadout) |
| Worker | Builds everything, mines gold | No loadout |

Skills such as spotter and heavy anti-tank come with later archetypes; the medic came as the Sentinel's assault medic ("Assault medic", below).

**Vehicles and crew.** Vehicles leave the yard empty. Any of your soldiers can drive one, every driver is equal, and without a driver a vehicle can't move or fire. When a vehicle is destroyed, the driver survives by a chance drawn from the match seed. No capturing enemy vehicles.

| Vehicle | Role |
| --- | --- |
| Dirt bike | Fastest scout and raider, light weapon |
| Technical | Crewed armed pickup: a permanent MG gunner plus a driver; fast (about 3.5 cells/s) |
| Light rocket truck | Short-range salvos, \~6 cells |
| Medium rocket launcher | Medium-range salvos, \~12 cells |
| Heavy rocket launcher ("Rocket truck") | Area bombardment, 15 to 60 cells, slow, needs a spotter; see below |
| Cargo truck | Carries 400 supply to a depot and unloads it in 3 s; reloads the howitzer, 20 supply a shell, keeping the rest |
| Infantry carrier | Armoured 6x6 troop truck: a driver plus 15 infantry, who can't fire out; 2.8 cells/s; survives any single strike but the ballistic missile and a full heavy rocket salvo |

**Spotters and fog of war (2026-10-18, the owner's calls).** Launchers "need a spotter" below. Without fog of war (`MatchSetup.Fog` off, the default and the golden replay) the player's attack order is enough: an ordered attack fires at any target the player sees on screen, and only units picking targets on their own need a friendly unit to see the target. With fog on, "sees on screen" means "spotted by your side", so ordered attacks need a spotter again.

**Fog of war (2026-10-18, `MatchSetup.Fog`, simcore `Sim/Fog.cs`).** Each player sees only what its units see. Enemy units nobody on its side spots are invisible to it: not drawn, not selectable, not targetable, not commandable. Unexplored ground is dark; explored but unseen ground is grey, with last-seen ghosts of enemy buildings (client memory). Line of sight: ridges and woods block ground units' vision (eyes 1.7 m for soldiers and 2.5 m for vehicles, structures and garrisons from the roof; you see about 2 cells into dense forest, 4 into a grove, 8 into scrub; tuned in the "vision" block of `content/terrain/rules.json`). Air units (drones, the coming scout drone) see their whole vision disc over ridges and woods, and air units are spotted by distance (the sky is never hidden) or by an air defence unit's radar (`airVision`). Since 2026-10-25 a soldier or vehicle under grove or dense canopy is seen from the air only as the air unit's sensors allow ("Concealment from the air", under "Trees as obstacles"). Shells and blasts hit what is there, seen or not. Each player has a visible/explored grid per cell (`World.FogOf`), which the renderer and the future minimap draw from. A simple skirmish AI (`Bots/SkirmishAi`) plays any player slot honestly: it knows only what its side spots.

**Observation post (2026-10-18).** Built by workers; a low camouflaged bunker that stays its builder's (`garrison.owned`), holds 3 soldiers who fire out and come out of its ruin with at most 40% HP. It sees 16 cells, plus 5% for every metre it stands above the mean ground within 32 cells, up to +60% (`highGround`). With a sniper inside it launches the scout drone (it carries `scout_station_launch`, usable only while a sniper is in: `garrison.launcherTags`; one drone aloft at a time). Tagged `stealth`: the enemy's minimap shows it only while spotted.

**Rocket truck (heavy launcher).** A truck with a 30-tube launcher. It must **deploy** (about 2.5 s, stabiliser jacks down) before firing and **stow** (about 2 s) before driving. It fires only on orders (no automatic targeting), between a 15-cell minimum and a 60-cell maximum range, and needs a spotter: some friendly unit must see the target. A salvo **ripples 30 rockets, one every 0.5 s**, scattered about 3.5 cells; Stop ends a salvo at once. **Reload only at a supply depot**: never during a salvo, starting 1 s after the last shot, then one rocket every 2 s while it stands in the depot's supply radius. Splash falls off from the centre to the edge, so a full salvo on a clustered group kills about half the infantry and badly damages vehicles. Blasts are bigger area ground blasts that leave 3 s of ground fire and a large burnt-ground mark.

**Twin missile truck.** Two heavy missiles side by side, fired 6 s apart, from 45 to 100 cells on a high arc. It deploys in about 6 s and stows in about 5 s, fires only on orders and needs a spotter. Very high damage: 2 missiles bring down a civilian block. A small circle (3 cells) where a missile lands kills everything; a big circle around it (out to 9 cells) takes 30% of that, which still kills infantry. After firing it must go back to a depot and reload stowed, one missile every 10 s for 150 supply each. Numbers are content data for tuning.

**Infantry carrier (2026-10-18).** A long-nose 6x6 troop truck: a driver seat plus 15 passenger seats for any infantry (workers too; only soldiers drive). Infantry ordered to enter it walk to it and climb in, the first into an empty driver seat. Riders can't be seen, hit or fire out: no fire ports, unlike a garrisoned building. Unload one chosen rider (out behind the truck) or all of them (the driver stays at the wheel). Speed 2.8 cells/s, between the cargo truck (2.2) and the technical (3.5): faster than any launcher, slower than the raiders. It is armoured: 1200 HP in its own armor class, so the worst first strike of every other weapon falls short even if every round hits (twin missiles 2 x 550 = 1100, rocket jeep 2 x 540 = 1080, the rocket truck's 30 rockets 30 x 39 = 1170, one heavy rocket 432). The tactical ballistic missile has its own warhead (the heavy missile's table plus 170% against armour): 1241 inside its inner ring destroys it (an outer-ring landing, 30%, does not), and a full 12-rocket heavy salvo destroys it. When it is destroyed, the driver and then each rider roll the vehicle crew's survival chance (50%) and survivors lose 40% of max HP, the same rule as any driver; while it stands, riders take no damage at all (unlike a building's occupants). Numbers are content data for tuning.

**Tactical missile truck.** One ballistic missile, from 60 to 160 cells: it aims for 10 s standing still while the erector rises, and moving cancels the aim. The missile is very fast (1.8x the twin missile) and comes down from a high apex, so it should be hard to intercept once interceptors exist. It hits about 1.3x as hard as a twin missile, with rings 10% bigger: a brick block falls in one hit. After firing it must go back to a depot and reload stowed, which takes 60 s for 250 supply. It is the slowest launcher on the road. Numbers are content data for tuning.

**Suicide drones.** These are one-way attack drones fired at a point on the ground, fixed at launch. Each one leaves its rail with a booster, climbs to 30 m above the map's highest ground, cruises there slowly (12 cells/s, up to 600 cells) and dives onto the point. A mountain in its climb stops it; in the dive, the first wall, vehicle or soldier on its path sets it off. It kills infantry and wrecks most vehicles in a 4-cell blast, and each drone takes 20% of a building's max HP, so 5 bring any building down. The drone launcher truck carries 2 drones and fires one at each of up to two designated points (G, then Shift+G), reloading at a depot (30 s, 150 supply each). An engineer can set up a drone station in 2 s: it holds one drone that its owner fires remotely, even after the engineer leaves or dies, and its empty rail goes 20 s later. Numbers are content data for tuning.

**Rocket jeep.** A fast light 4x4 (the technical's speed, faster than every other launcher;
170 HP, medium vehicle, 800) with two short-range heavy rockets: a shoot-and-scoot raider. It
deploys in 3 s and stows in 2 s, fires only on orders and needs a spotter: 2 rockets 3 s apart
from 20 to 50 cells, 450 damage with the heavy rocket warhead (900 to buildings: 2 hits bring
down a brick block, 3 a panel block), a 4-cell splash falling from 100% to 20%, 3 cells of
scatter. It reloads only at a depot and only stowed: one rocket every 6 s for 80 supply.

**Kornet team (guided missile).** A two-man guided anti-tank missile team: slow on foot
(0.8x the anti-armor soldier), 55 HP, 500. It aims for 1 s, standing still, before every shot,
then guides the missile (about 20 cells/s, range 28.8 cells) onto its target: the missile turns
toward it each tick (a limited, integer turn rate), so it hits vehicles that drive away or
across. Guidance lasts up to 36 cells of flight (range + 25%); it ends when the target dies or
gets farther than that from the operator, or when the operator moves or gets another order
(SACLOS: he must keep still); the missile then flies straight on for 3 cells and hits the
ground. It hits only its target, never infantry in the way. A hit takes about 70% of a
technical (126 of 180), about 30% of a launcher (69: never a one-shot), barely hurts infantry
(18), and a building takes what an RPG hit does (56). It reloads in 5 s once it stops guiding,
then aims again. On its own it picks vehicles first, then buildings, then infantry. Engine:
weapon `aimTicks`, `guided` (`maxFlight`, `turnRate` in degrees per second, `requiresOperatorStill`,
`dropDistance`) and `priority` (armor classes in order).

**Mortar soldier (2026-10-18).** One soldier carrying a light mortar (tube, bipod, round base
plate) and a pack of 10 bombs: indirect fire for infantry (`mortar`, weapon `mortar_tube`, warhead
`mortar_bomb`). The owner's calls: attack ground (G) at any point in range, **into the fog too**: he
needs no spotter to fire at a point, only to see the target and the results, and to attack a unit
(an attack order on a unit needs it spotted, like every ordered attack under fog). Firing at a point
keeps going until he is stopped, re-ordered or out of bombs. Numbers (content data, for tuning):

- **Range 8 to 30 cells.** Half the rocket truck's 60; about the Kornet's 28.8, 2.5x the sniper's
  12, half the rocket jeep's 50: it out-ranges every other infantry weapon. Inside 8 cells he never
  fires (a point there is refused; he does not set up or walk away); beyond 30 he walks closer first.
- **Indirect fire.** The round flies a high arc whose apex is 45% of the distance (weapon `arc`),
  so it needs no line of fire and comes down over ridges and woods that stop a rocket truck's lower
  arc; a ridge right at the target can still catch it. 8 cells/s: 3.75 s of flight at full range.
- **Set-up and rate of fire.** 5 s to set up (deploy) before the first round, 3 s to pack up before
  he can walk; then 1 s to lay the tube before every round (`aimTicks`, standing still) and 3 s
  between rounds: **one round every 4 s** (15 a minute), the first 6 s after the order. He fires only
  on orders, like the launchers.
- **Scatter grows with range** (weapon `scatterNear`): 0.8 cell per axis at 8 cells, rising linearly
  to 3 cells at 30 (about a tenth of the distance), so close work is accurate and far work is area fire.
- **Damage.** 90 a bomb, a 2.5-cell splash falling from 100% at the centre to 20% at its edge. The
  `mortar_bomb` warhead: 100% to infantry (a direct hit kills any soldier; within about a cell a
  rifleman dies, within 1.5 a sniper or worker), 45% to light and medium vehicles (40 a hit: a
  technical takes five, a rocket truck six), 10% to armour (9 of the carrier's 1200), 30% to buildings.
- **Ammo.** 10 bombs; only a supply depot refills them: one every 2 s for 10 supply (like a rocket
  truck's rocket), starting 5 s after the last round, which is longer than the 4 s between rounds, so
  a mortar firing beside a depot still runs dry and pauses (a full refill: 25 s, 100 supply).
- **In buildings: only from an observation post** (the owner, 2026-10-18). Inside any other building he
  holds fire (orders to fire are refused); in an observation post he sets up inside, whatever the
  ground (the post is a prepared position), and fires from it with the post's +1 cell of range.
  Data: the post's garrison lists `deployTags: ["mortar"]`; any unit that must set up to fire
  (`deployTicks`) fires from inside only where its garrison lists one of its tags.
- **The soldier.** 50 HP, 6 cells of sight, the slowest soldier (1.3 cells/s against the Kornet
  team's 1.4 and the rifleman's 1.9: the load), cost 400.

Damage compared (direct hits; "a round" is the mean of many at the given scatter, on flat ground):

| Weapon | Range (cells) | A shot: soldier / technical | Rate | Mean on a lone soldier / technical a shot | A minute |
| --- | --- | --- | --- | --- | --- |
| Rifle (one soldier) | 5 | 12 / 4 | 0.75 s | 12 / 4 (instant, always hits) | 960 / 320 |
| RPG | 9 | 28 / 84 | 3.5 s | about the same (aimed, leads) | 480 / 1440 |
| Mortar at 10 cells | 8-30 | 90 / 40 | 4 s, 10 a load | 68 / 34 | 1020 / 500 |
| Mortar at 30 cells | | | | 23 / 11 | 340 / 160 |
| Rocket jeep | 20-50 | 450 / 450 | 2 rockets 3 s apart, then the depot | 240 / 245 | a pair a depot trip |
| Rocket truck | 15-60 | 75 / 150 (splash centre 9 / 18) | 30 in 15 s, then the depot (60 s) | 3 / 11 | a salvo: 94 / 336 |

On a group of six soldiers packed in 3 x 2 cells, a mortar round does about 340 at 10 cells and 135
at 30 (a 30-rocket salvo about 560 in 15 s): over its 10 bombs (40 s) it wipes out a group that stays
put, and anyone who moves off between rounds escapes most of it. Against vehicles it is a nuisance.
Balance against its reach: it needs spotters to see, 6 s before the first round and 3.75 s of flight,
a slow rate, wide scatter far out, a short magazine and the depot.

**Rocket fleet and supply.** Launchers carry a few salvos (count set in playtests) and go quiet when empty. Only supply depots rearm them; units in a depot's supply radius reload faster. Launchers need someone to see the target. A selected depot shows its supply radius as a soft green ring (about 40% opacity) and carries a floating 3D ammo emblem; a selected rocket truck shows its side's depot radii, where it reloads.

**Depot stock and cargo trucks.** A depot is not unlimited: it holds 1000 supply, starts full and never refills by itself. Every salvo or charge it hands out costs 1; launchers that only a depot reloads pay per rocket: the rocket truck 10 (1%, a full 30-rocket reload is 30%), the heavy rocket truck 40 (4%, a full 12 is 48%). When the stock is below a rocket's cost the launcher waits at the depot. Cargo trucks refill depots (trucks no longer rearm units themselves, but for the howitzer's shells: "Howitzer", below): a cargo truck carries up to 400 supply (40% of a depot; it was full or empty until the howitzer took part of a load); sent onto a friendly depot (or stopping in its radius) it unloads in 3 s, standing still, and the whole load goes in at once. If the depot has less than 400 free, the truck waits beside it until there is room, so nothing is wasted; driving off or losing the depot cancels the unload and the truck stays full. Empty trucks stay empty for now; supply routes come later. All numbers are content data for tuning.

**Drones.** An engineer kit, not a built unit: 2 FPV drones per engineer. Pick a target and the drone flies there on its own and explodes. It can be shot down in flight: the AA missile is the hard counter, and rifles and MGs hit drones only weakly. Spent drones restock at a supply depot.

**Tunnels** (owner's spec, 2026-10-18; `Sim/Systems/TunnelSystem.cs`, `TunnelGround.cs`, tests `TunnelTests`). Workers build three entrances (no cost yet): a **large tunnel** (7x7 cells, every ground unit, 40 slots, 1 unit a second through its door, 1500 HP, visible like a building, 40 s on ground of at most 10%), a **medium tunnel** (5x5, infantry and light and medium vehicles, 20 slots, 1 per 1.5 s, 1000 HP, 25 s) and an **infantry hatch** (2x2, infantry, 8 slots, 1 per 2 s, 500 HP, 10 s); entrances keep 12 cells apart. Units carry a size (1 infantry, 2 light and medium vehicles, 3 heavy: the 30-tube launcher, the heavy rocket, missile and ballistic trucks) and slots (1, 3 for the technical, jeep and bike, 5 medium, 8 heavy; a carrier 5 + 1 a rider); aircraft never go in. A side's finished entrances link within 60 cells and a connected network shares the sum of its capacities: over it nobody new goes in (overflow never kills). Units travel 3 cells a second, link by link; vehicles only through entrances of size 2 or more, so a medium entrance is a junction a heavy truck passes but can't come out of and a hatch never carries vehicles; a unit comes out only where the entrance is at least its size. Going in and coming out take the door's throughput, longest-waiting first; a blocked door pauses (idle friends step aside). Underground units are off the map (`Unit.Inside` = `World.Underground`): never seen, hit, ordered or spotting; commands `TunnelGo` (to an entrance, out there or waiting below) and `TunnelEvacuate` (everyone out by the nearest entrance they may use). **Destruction is two-stage:** a destroyed entrance becomes rubble with its own HP (half the entrance's): it keeps its capacity and still joins the network, but nobody passes in or out; units heading out there wait below. Destroying the rubble removes the node: units at it move to the nearest linked entrance they may pass, or die; that gives the **survival table** per connected section: with a large entrance left everyone lives and may come out; with medium entrances only heavy vehicles live but are trapped until a large entrance links up again; with hatches only vehicles are destroyed (their crews and riders are infantry below and live on); with nothing left everyone below is lost. Units coming out of an entrance as it falls lose 40% of their HP. **Stealth:** medium entrances and hatches are hidden from the enemy until a detector (sniper, scout drone, observation post) reaches their footprint; any unit passing a door shows the entrance for 10 s to enemies that see it there (fog: not through fog). **Ground:** placing an entrance reshapes the terrain in its frame: footprint and apron flat at their mean height, the earth at the mound height behind the headwall (dropping to the floor inside it, so the portal's recess stands over flat ground), wing-wall tops on the outer columns, easing back to the land; when the land within 8 cells behind rises 15% or more it is built into the hillside (a taller mound and the hillside look), facing downhill by default. Once the rubble (or a cancelled or destroyed site) is gone the ground returns over 30 s. **Trees:** only the footprint and the apron (the door and the strip in front of it) lose their forest, standing trunks and any fallen trunk across them (`World.ClearTrees`; a hatch 2 x 3 cells = 4 x 6 m, a medium entrance 5 x 7 = 10 x 14 m, a large one 7 x 10 = 14 x 20 m); the trees on the blend ring stay standing and the ground eases under them, and the cleared cells stay clear when the ground returns (decisions 458 to 460). Foot workers place a hatch or a medium entrance in dense forest (any ground they can walk, as before) and a large one on flat forest ground; cars cannot reach or leave a medium entrance in dense forest, heavy trucks and tracked vehicles push their way (decisions 461 to 463). AI use of tunnels: later.

**Guardrails.** Hidden things such as traps are hidden only on screen: in lockstep every machine simulates them, so a hacked client could reveal them, as in every lockstep RTS.

### Kaman drone (2026-10-22)

A Kaman-22 type long-range attack drone for the Resistance (the owner's brief and answers of 2026-10-22; an MQ-9-like
airframe with two missiles under its wings). The one-way drone (above, "Suicide drones") is its template: its rules
hold unless said here. Content: `archetypes/spawned.json` (`kaman_drone`, `kaman_station`), `archetypes/vehicles.json`
(`kaman_truck`), `components/vehicle_weapons.json` (`kaman_rail`, `kaman_station_rail`, `kaman_missiles`),
`components/kits.json` (`kaman_station_kit`), `warheads.json` (`kaman_charge`), the air class `kaman`; code
`Sim/Systems/KamanSystem.cs`; tests `KamanDroneTests` (20). The owner's answers are marked (owner); everything else is a
default (rows 158 to 172 of "Decisions to confirm").

- **A unit, not a projectile.** The one-way drone is a projectile; the Kaman is a flying unit with a drone flight block
  (`flight`, air class `kaman`) and its own `kaman` block. That is what makes it, with no new code in those systems, an
  air target for every air defence weapon and the jets (they engage flying units of their classes), tracked by team
  cueing and the radar (`World.AirTracked`, `RadarTracks`), seen by the enemy in the sky like any air unit under fog,
  a spotter for its own side on its way (40 cells of sight over ridges and woods), and a falling wreck when shot down
  (its `loss` block). A projectile has no vision, no HP for a 2-hit rule and no place in the air-to-air picking. It
  takes no orders once launched.
- **Launchers (owner).** The Kaman launcher truck (`kaman_truck`: a medium 6x6 with one long rail, 220 HP, speed 2.2,
  1,800, 40 s from the garage) and the engineer's Kaman station (`kaman_station`, set up in 3 s with his
  `kaman_station_kit`, the Kaman option beside his drone station kit). Both fire with an **attack order on a unit or
  building** (it follows it) or **attack ground at a point**, from 60 to 1,200 cells (`kaman_rail`: the new weapon
  field `launches` names the unit its round is). The truck deploys in 2 s and stows in 2 s like the drone truck and
  reloads only at a depot, stowed: 60 s and 400 supply. The station holds one Kaman, never refills, never fires on
  its own (`holdFire`), and goes 20 s after it is spent; the engineer's kit refills at a depot in 30 s for 400. The
  drone truck and drone station are unchanged.
- **Flight.** Off its rail 3 m up, it climbs at up to 12 m/s (a second to full climb) to **150 m over the ground**
  (the scouts' height planner: it follows the terrain and clears roofs) and cruises at **15 cells/s** (the one-way
  drone 12), turning at 60 degrees a second toward its aim (the drone law of the scout drones). 100 s of fuel: 1,500
  cells of flight. Measured: 1,198 cells in 79.9 s; 150.0 m at the top (`ItReachesTwelveHundredCellsAndNoFurther`,
  `ItFollowsAMovingVehicle...`).
- **The target (owner).** While its side sees the unit it was launched at (the player's view: with fog of war, spotted
  by his side, its own camera included), it flies at it and follows it. **Once its side does not (out of sight, gone
  into something, dead), it flies on to the last position seen, exactly**, and its missiles and dive go there; if its
  side sees the unit again before it goes off, it takes it up again (`Lost`, `Reacquired` events). Measured: a carrier
  driving 64 cells across its line was followed into (`ItFollowsAMovingVehicle...`); a target lost at launch + 6 s is
  dived on at its last position within a cell, untouched (`ALostTargetIsFlownToWhereItWasLastSeen`).
- **Missiles (owner: 2, from about 30 cells, a second apart).** `kaman_missiles`: 126 through `atgm` (the Kornet's:
  126 to armour and light vehicles, 69 to a launcher, 56 to a building, 19 to soldiers in a 1-cell splash), fire and
  forget at 40 cells/s, guided for 45 cells, fired only within 30 degrees of its nose, at its target while seen, else
  at its aim point. Measured: the first leaves 29.1 cells out (level distance), the second 1 s later 15.9 cells out;
  a technical (180 HP) dies to them before the drone arrives, which then dives where it was
  (`TheMissilesLeaveFromAboutThirtyCells...`).
- **The dive (owner: about 2x a one-way drone).** It pitches over when the aim is as close as its height needs at a
  45-degree slope plus half the 2 s pitch-over (about 90 cells out from 150 m: the missiles leave in the dive), then
  flies the straight line onto the aim, steering on. In the dive its target, else the first unit or roof under it
  (the one-way drone's rule), or the ground sets it off: **520 through `kaman_charge` in a 6-cell blast** (100% to
  40% at the edge): soldiers and every vehicle near it die, armour included (250%: 1,300 on a direct hit against the
  infantry carrier's 1,200), and **a building loses 45% of its max HP** (`maxHpPercent`; the one-way drone 260, 4
  cells, 20%). Measured: a panel block 2,400 -> 1,208 (1,080 from the dive, 2 x 56 from the missiles); a carrier on
  the move destroyed (`ItsDiveKillsArmour...`, `ItKillsInfantryAndVehicles...`).
- **Survivability (owner: shootable by all air defence, about 2 AA-soldier hits, 1 air defence vehicle hit).** 200 HP,
  armour `drone`; every intercepting weapon lists `kaman`: the AA soldier's Igla 70% at best and **a hit takes 50%
  (2 hits)**, the air defence vehicle 90% at best and **a hit kills**, the Patriot type 50%, the jets' missiles 90%,
  their guns 70% (the attack jet's 40%) a burst (a burst kills: 90 x 400%). Measured over six seeds, crossing a ring
  of 8 AA soldiers: brought down 3 times, always on the second hit (each shot 17 to 32%: head-on, 15 cells/s); a ring
  of 3 air defence vehicles: 6 of 6, each on the first hit (47 to 55% a shot); a multirole on Guard brings it down
  (`AirDefenceBringsItDown`, `AJetOnGuardShootsItDown`).
- **For renderers.** `Unit.Kaman` (`KamanState`: `Target`, `Aim`, `Lost`, `Phase` Climb / Cruise / Dive, `Fired`,
  `FireWait`, `Detonated`), its `Altitude`, `Vel` and `VelZ`; `World.KamanEvents` each tick (`Launch` with the
  launcher, `Missile` with the projectile, `Lost`, `Reacquired`, `Dive`, `Blast` with the unit hit); its missiles are
  ordinary guided projectiles (`Shooter` the drone). A blast is not an air loss; a shoot-down is (`AirLosses`, a wreck).

### Mobile radar (2026-10-22)

The Resistance's mobile air search radar (the owner's brief of 2026-10-22: "a radar that detects air presence over a very
large area, and helps the air defence systems and Igla holders spot too"): an SA-6 "Straight Flush" type, the 1S91 of the
2K12 Kub, a light tracked chassis with a tall drum carrying the flat surveillance antenna and the round tracking dish.
Content: `archetypes/vehicles.json` (`mobile_radar`), its own line in `factions/resistance.json`; code
`Sim/Systems/RadarCueSystem.cs` (the cued lock and the client queries) and small changes in `AirDefenseSystem.cs` (the
cue table's cueing-radar bit, the lock check); tests `MobileRadarTests` (21). Every number is content; the decisions are
rows 173 to 182 of "Decisions to confirm".

- **The vehicle.** A light tracked vehicle, `medium_vehicle` armour, 280 HP, speed 30 (the air defence vehicle's: it keeps
  up with the air defence column), 6 cells of sight, no weapon, crewed like every vehicle (a driver; it is no threat on
  its own). From the vehicle garage: 40 s, 1,600. It shows up to the enemy like any vehicle (no emitter rule).
- **Set up to search.** It searches only set up: a Deploy order sets it up in **8 s** (the drum rises, the antennas start
  turning), Stow packs it up in **6 s**, and any move packs it up first (the deploy system's rule: the rocket trucks',
  the radar truck's). Packed up, setting up or moving it has only its ordinary sight. Measured: set up at tick 160 to
  162 after the order it tracks a jet 200 cells out, and from the move order on its side tracks nothing
  (`PackedUpOrMovingItDoesNotSearch`).
- **Air search out to 400 cells.** Its `airVision` is 400 cells, a slant from its antenna 7 m up (its `height`), scaled
  by each target's signature like every air sensor (wider than the Sentinel radar truck's 360): a multirole about 391
  cells of slant out is tracked, one about 410 out is not; the air superiority jet (detect 50) is tracked within 200
  cells and not at 210 (`ItDetectsAircraftOutToFourHundredCellsBySignature`). Air targets are never hidden by ridges or
  woods. It tracks every air target its side's air defence knows: aircraft, helicopters, drones and the Kaman, and the
  one-way drones (projectiles).
- **It helps the air defence spot (team cueing, unchanged).** What it tracks is tracked for its side
  (`World.AirTracked`): the AA soldiers and the air defence vehicles pick and lock targets **inside their own weapon's
  range** that they don't detect themselves. Measured with the air superiority jet orbiting 85 cells up: an AA soldier
  (he detects it within 75 cells; it never came nearer than 109) fired at it from 134 cells of slant; an air defence
  vehicle (its own radar: 130) from 199 (`ItCuesAnAaSoldier...`, `ItCuesAnAirDefenceVehicle...`). Alone, neither ever
  tracked it.
- **The radar-cued lock (`cueLock` 70).** A ground air defence weapon of its side whose target a set-up mobile radar
  tracks locks in 70% of its own lock time, so the radar is worth having even when the shooters see the target
  themselves. Measured (the tracking ticks at launch, a multirole 30 cells out): the AA soldier **30 -> 21 ticks (1.5 ->
  1.05 s)**, his launch 9 ticks earlier; the air defence vehicle **20 -> 14 (1 -> 0.7 s)**, 6 ticks earlier; on the
  air superiority jet (lock 200) the vehicle 40 -> 28, the soldier (cued only: he never sees it) 60 -> 42
  (`ARadarCuedShooterLocksThirtyPercentFaster`, `ACuedSoldierLocks...`). Another sensor (an air defence vehicle's radar)
  or a packed-up mobile radar gives no faster lock (`OtherSensorsGiveNoCuedLock`). The kill chance is the shooter's own
  (the tracking bonus counts from the shortened lock; `TheCuedLockLeavesTheKillChanceAsItWas`).
- **Never more range.** Firing still needs each weapon's own range: with a multirole orbiting 228 cells and more from an
  AA soldier (140) and 238 from an air defence vehicle (220), tracked by the radar for 30 s, the soldier never picks it
  and nobody fires (`ItNeverAddsRange`).
- **Under fog.** Set up, it spots air units for its side within the same reach (the existing air vision rule): a
  multirole 350 cells out with nobody else of its side near is seen by its side, and its cued AA soldier engages a jet he
  can't see himself; packed up, the far jet drops out of sight (`UnderFogTheSideSees...`).
- **For renderers.** `World.RadarOf(u)` (`On`: set up, built, alive; `Air` 400 cells, `Ground` 0, `Sight` 6, `CueLock`
  70: a new init property, 0 for every other radar); `World.RadarTracks(u)` (the enemy air units it tracks now, in ID
  order; empty unless set up); `World.RadarSweep(u)` (its antenna's bearing in thousandths of a turn clockwise from
  north, one turn every `sweepTicks` 80 = 4 s, phased by its ID, the same on every machine; -1 when off: no state);
  `World.RadarEyeRaw(u)` (the antenna's absolute height, raw: the sweep's origin); `World.RadarCuedShooters(u)` (its
  side's ground air defence units whose target it tracks now: the cue lines); `World.RadarCued(shooter)` and
  `World.AirLockTicks(shooter)` (the lock it needs now, cue included, against `Unit.TrackTicks`; with `Unit.AirTarget`);
  `Unit.Deploy` and `DeployLeft` for the set-up animation; orders `CommandKind.Deploy` and `Stow`.

### SA-6 launcher (2026-10-22)

The Resistance's medium-range air defence launcher (the owner's brief of 2026-10-22: "a tri-missile air defence vehicle; a
wide range, wider than the Osa; 3 missiles only; does more damage than the Osa; the same HP"): a 2K12 Kub type, the 2P25
launcher, on the mobile radar's tracked chassis with a low turntable at the rear carrying 3 large missiles side by side.
Unlike the Osa (the air defence vehicle: launcher and radars in one) it is a **separated battery**: it depends entirely on
the mobile radar ("Mobile radar", above). Content: `archetypes/vehicles.json` (`kub_launcher`), `components/vehicle_weapons.json`
(`kub_missiles`), its own line in `factions/resistance.json`; code `Sim/Systems/BatterySystem.cs` (the battery link, the
semi-active loss, the client queries) and small changes in `AirDefenseSystem.cs` and `SupplySystem.cs`; tests
`KubLauncherTests` (29). Every number is content; the owner's are marked (owner), the rest are rows 183 to 193 of
"Decisions to confirm".

- **The vehicle.** Tracked, `medium_vehicle` armour (the radar's chassis), **280 HP (owner: the Osa's)**, speed 30 (the
  radar's), 6 cells of sight, a turret (the turntable turns to the target), a driver like every vehicle. From the vehicle
  garage: 45 s, 2,000 (1,800 + 200 for the missiles). It has no air vision of its own.
- **Set up to fire.** Deploy sets it up in **5 s** (the arm elevates), Stow packs it up in **4 s**, any move packs it up
  first; it fires only set up, in Guard (or Hold ground, Aggressive), idle and standing, like the Osa. Measured: ordered to
  set up beside a radar tracking a jet, it fired 116 ticks (5.8 s) later (`PackedUpItHoldsFire`).
- **The battery link.** It fires only at an air target that a **linked radar** tracks: a set-up mobile radar of its side
  (any unit of its side with an air vision and the tag `radar`, working: alive, built, not inside, set up) standing within
  **60 cells** (level) of it. The radar must track the target itself (its 400 cells by signature), not just its side's team
  cueing: an Osa of its side tracking a jet 120 cells out with its own radar, and a mobile radar packed up beside it, leave it
  silent for 30 s; set the radar up and it fires (`WithoutALinkedRadarItHoldsFire`). A radar 58 cells away links, one 62
  away does not, though it tracks the jet (`ItLinksOnlyToARadarWithinSixtyCells`). Without a linked radar it holds fire. The
  mobile radar's cued lock applies (1 s -> 0.7 s). Data: the interceptor's `battery` block (`linkRadius`, `radarTags`,
  `semiActive`), exclusive with the Patriot type's `radarLinked` (a radar there is a unit with a `groundRadar`; the mobile
  radar is not one, decision 179).
- **Semi-active radar homing.** The missile homes on the radar's illumination: each tick of its flight some radar linked
  to its **launch point** (60 cells of it, set up, of its side) must still track its target. Once none does (packed up,
  destroyed, the target out of its reach) the missile loses its guidance for good, flies on straight to its max flight and
  blows itself up there: it cannot fuse. Measured on a multirole 273 cells out: the radar packing up or destroyed 1 s after
  the launch loses the missile that very tick (it self-destructs, the jet flies on); a second linked radar still tracking
  keeps it guided to the fuse; the launcher destroyed in flight does not matter (`TheMissileIsLostWhenNoLinkedRadarTracksTheTarget`).
  A lost missile no longer counts as flying at the target: the launcher fires again once a radar tracks it
  (`ALostMissileDoesNotHoldTheTarget`).
- **Reach (owner: wider than the Osa, about 1.6x).** 350 cells of slant (the Osa's 220 x 1.6), from 15 cells, at **55
  cells/s** (the Osa's 45), guided for 480 cells. Measured: a multirole orbiting about 300 cells out was engaged at 321 cells
  of slant while the Osa beside it, cued by the same radar, never had it within 220 (`ItOutReachesTheOsa`). **Higher (owner):**
  neither has a height ceiling (the Osa never had one); the height comes with the slant reach: against the strategic bomber
  400 m (200 cells) up the Osa reaches only within about 92 cells of ground, the launcher out to about 287; measured, it
  launched 251 cells out over the ground (321 of slant) at the bomber 402 m up (`ItReachesTheBomberHigherAndFurtherOut`).
- **More damage (owner: more than the Osa).** The Osa's kill model with its per-class best chances and shares of HP a hit
  about 1.3x, capped: `killBest` 91 against the jets and the attack and armoured helicopters (the Osa's 70), 95 (the cap,
  `killMax`) against the attack jet, the gunship, the bomber, the light helicopter and the Kaman, 26 / 52 against the scout
  and Watcher drones; `hitDamage` 65 attacker (2 hits as before), 17 gunship (6 hits, the Osa's 8), **22 bomber (5 hits, the
  Osa's 6)**, 44 attack helicopter (3), 34 armoured helicopter (3, the Osa's 4); the Overseer 85% a hit (65). Measured on one
  shot through both models: the launcher's chance over the Osa's is 1.30x on the multirole and the fighter, 1.27x on the
  attack jet, 1.19x on the gunship and 1.12x on the bomber (`ItsKillChanceIsAboutOnePointThreeTimesTheOsas`); four
  launchers brought an orbiting bomber down in five hits, 2,000 -> 1,560 -> 1,120 -> 680 -> 240 -> 0
  (`FiveHitsBringTheBomberDownAgainstTheOsasSix`). It engages the Osa's air classes except the FPV drone.
- **3 missiles (owner).** One every 2 s at most, one at a target at a time; measured launches 84 and 77 ticks apart at a
  bomber, then empty (`ThreeMissilesThenItIsEmpty`). Chaff answers it like the Osa's radar missile (a multirole's salvo cut
  the chance to 35%; `JetsAnswerItWithChaff`).
- **Reload.** Only at a supply depot, **packed up, one missile every 20 s for 200 supply**, 1 s after the last shot. Idle
  and set up in a depot's radius with a missile short it packs up by itself (the launchers' rule) but **not while it tracks
  an air target**: there it fires all three first (`AtADepotItStaysSetUpWhileItTracksATarget`). Measured: packed up 81
  ticks after it ran dry at the depot, then a missile at +399, +799 and +1,199 ticks, the depot 1,000 -> 800 -> 600 -> 400
  (`ADepotReloadsItPackedUpOneMissileEveryTwentySeconds`). It does not set up again by itself.
- **For renderers.** `World.BatteryRadar(launcher)` (the nearest working linked radar now, or null: no link, it holds fire);
  `World.BatteryLaunchers(radar)` (the launchers of its side it links now, in ID order: the link lines);
  `World.BatteryOf(def)` (the battery block: `LinkRadiusRaw` for the link circle, `RadarTags`, `SemiActive`; the weapon's
  `Range` and `MinRange` for the range circle); `World.BatteryLost` (the missiles that lost their guidance in the last tick;
  each then has `Projectile.Guided` false and ends in a `SelfDestruct` or `Ground` air burst); `Unit.AirTarget`,
  `TrackTicks`, `World.AirLockTicks`, `RadarCued` as for the Osa; `Unit.Deploy` and `DeployLeft` for the arm; orders
  `CommandKind.Deploy` and `Stow`. No new state (a lost missile's `Guided` was already hashed).

### Howitzer (2026-10-24)

The Resistance's M777 type towed howitzer, dug in (the owner's brief of 2026-10-24: "fixed in place, built by a worker;
about 15 high-power shells when built; a heavy hitter with area damage; reloaded by a supply drop from a cargo truck that
doesn't need to take the truck's whole load"; and his answers on spotting, crew and the flash). It is the first structure
that fires. Content: `archetypes/structures.json` (`howitzer`), `components/vehicle_weapons.json` (`howitzer_155`), its own
line in `factions/resistance.json`; code `Sim/Systems/HowitzerSystem.cs` (the truck reload, the flash, the events and
queries) and small changes in the crew, combat, order, command, supply and vision systems; tests `HowitzerTests` (24 tests, 25 cases).
Every number is content; the owner's are marked (owner), the rest are rows 294 to 313 of "Decisions to confirm".

- **The emplacement.** A structure **built by a worker (owner)**: 10 x 10 cells (20 x 20 m: the gun with its four trail
  legs, the sandbag ring, a camo net, the shell pallet), 40 s, 1,500, **1,200 HP of `building` armour**, 4 cells of sight
  past its footprint (it relies on spotters), tags `structure` and `artillery`, glyph `Z`. Measured: one worker finishes it
  in about 780 ticks with 15 shells aboard; a site takes no crewman
  (`AWorkerBuildsItInFortySecondsWithFifteenShellsAndNoCrewUntilFinished`).
- **The crew (owner: 1 soldier).** A crew seat on a structure (the vehicles' `crew` block, one seat, no passengers): any
  soldier of its side walks to within 1.5 cells of its footprint and takes the seat (`Enter`), never a worker or an enemy;
  without him it never fires and takes no orders; `Exit` puts him out past the middle of its south edge and it falls
  silent; **any soldier can take over (owner)**. The crewman is in an open pit: he takes 15% of an explosive hit on the gun
  (the observation post's share; `crew.occupantDamage`, `crew.occupantWarheads`) and can die in the seat, leaving the gun
  standing and silent until another soldier takes it (`AnExplosiveHitCanKillTheCrewmanAndLeaveTheGunSilentButStanding`).
  **Destroyed, he bails out by the usual crew rules (owner)**: 50% to live, 40% of his HP lost; over 12 seeds 6 lived
  (`DestroyedItsCrewmanBailsOutByTheCrewRule`).
- **The gun.** **15 shells (owner)**, **300 damage** with the heavy rocket's warhead (600 to buildings, 360 to armour: two
  shells bring down a howitzer, `TwoShellsBringDownAHowitzer`), a **3.5-cell splash** from 100% at the centre to 10% at its
  edge and a small runway crater (the heavy rockets'), **15 to 80 cells** in the content's raw scale of the heavy rocket
  truck (50 to 90) and the mortar (8 to 30), counted from its centre (a structure's weapons fire from its centre, so the
  range and minimum range rings are circles), **one shell every 8 s** (15 last 112 s after the first, measured), a **slow,
  high arc** (apex 50% of the distance, 10 cells/s: measured 33 ticks of flight at 16 cells, 160 at 79, so a target that
  turns or stops drives out of it; `TheShellFliesSlowlyOnAHighArc`), **scatter growing with the distance**, 1 cell per axis
  at 15 cells to 3 at 80 (`ScatterGrowsFromOneCellNearToThreeAtFullRange`), top attack on directional armour (a lobbed
  round), and shells that air defence taking rockets can shoot down (the `rocket` class: the Patriot type). A soldier where
  a shell lands dies; one 7 cells off is untouched (`AShellKillsWhatItLandsOnAndSparesWhatStandsPastItsSplash`).
- **Traverse.** All round, slowly: one facing (11.25 degrees) every 8 ticks (archetype `traverseTicks`), half a turn in 6.4
  s; it fires within a facing of its target; idle, it stays laid where it is. Measured: a point due behind it took its
  first shell 113 to 130 ticks after the order (`ItTurnsSlowlyHalfATurnInAboutSixSeconds`).
- **Spotting (owner).** On its own it fires only at enemies a friendly unit, observation post or scout drone sees (its side
  spots them), in its rings, **preferring groups and structures** (weapon `groupRadius` 3.5 cells and `groupStructure` 3:
  the candidate with the most enemies it could hit round it goes first, a structure counting 3, after the usual attacker
  and threat rules; `OnItsOwnItPrefersTheBiggestGroup`). A target 50 cells out with nobody near it is left alone; a
  worker walks up and the gun turns and fires (`OnItsOwnItFiresOnlyAtWhatASpotterSees`). **Attack ground (G) fires
  anywhere in its rings, seen or not, with the same accuracy (owner)**, until stopped or out of shells; a point inside 15
  cells or past 80 is refused (`ItTakesOnlyPointsBetweenItsMinimumAndItsRange`). **Shift+G** queues points (any ground unit
  on an attack-ground order, up to 16, `Unit.GroundQueue`): one shell at each in turn, round and round
  (`AShiftQueueOfPointsTakesAShellEachInTurn`).
- **Reload: only by cargo truck (owner).** A cargo truck of its side sent to it (`Unload`, its target the gun) or standing
  idle and still within 15 cells of its footprint hands it **one shell every 2 s for 20 supply** until the gun is full or
  the truck has less than 20. **The truck keeps the rest (owner)**: a full refill of 15 costs 300 and the truck drives off
  with 100, which it can unload at a depot as before or take to another gun (`ACargoTruckHandsOverOnlyWhatItNeedsAndKeepsTheRest`,
  `OneTruckServesTwoHowitzersAndTwoTrucksFeedOneGunNoFaster`). A truck parked 12 cells off reloads it; one 22 cells off does
  not (`ATruckParkedNearbyReloadsItAndOneFartherAwayDoesNot`). One truck feeds a gun at a time (two trucks are no faster),
  a truck one gun at a time. Nothing else reloads it: beside a depot for 50 s it gets nothing
  (`ATruckShortOfAShellHandsOverWhatItCanAndNothingElseReloadsTheGun`). So a truck's load is now anything from 0 to 400
  (it was full or empty): a depot still takes whatever is aboard at once, `LoadCargo` tops a truck up.
- **The flash (owner: about 10 s).** Every shot shows it to every enemy for **200 ticks** (the next 200 vision passes),
  through the fog and smoke, a target as if spotted, started again by each shot: two shells 8 s apart kept it spotted for
  exactly 360 ticks (`EveryShotShowsItToTheEnemyForTenSeconds`); an enemy mortar 22 cells off that could not see it took it
  as its target after the first shot (`TheFlashBringsCounterBatteryFire`). Weapon `reveal`; `Unit.RevealUntil`.
- **For renderers.** `World.EmplacementOf(gun)` (rounds n/15, max, crew, `Manned`, reveal left, `Revealed`, the truck
  feeding it and ticks to its next shell, reload left); `World.EmplacementEvents` (`Fired` with the aim after scatter, the
  rounds left and the projectile ID; `Revealed` with the ticks and whether it was hidden before; `RoundIn` with the rounds
  and the truck's supply left; `ReloadEnded`; `CrewIn`; `CrewOut`; `CrewBailed` lived or died; `CrewLost`);
  `World.RevealLeft`, `World.RevealedBy(gun, player)` (the enemy's flash marker), `World.FeedingOf(truck)`,
  `World.CanFeed(truck, gun)`, `World.ShotAt(gun, point)` (in range, too close, out of range: the rings and the G cursor);
  the weapon's `Range` and `MinRange`; `Unit.TurretFacing` (the traverse), `Unit.Cargo` (a truck's supply left),
  `Unit.Driver` (the crewman). The art's nodes: a traverse node, the barrel's elevation node and muzzle point, a recoil
  node, the crewman's spot; the footprint is 10 x 10 cells (20 x 20 m), its front (Exit) to the south.
- **Hashing.** `FeedTo`, `FeedLeft`, `RevealUntil` and `GroundQueue` only while one is set: games without them hash as before.
  Deterministic over two runs and with the spatial index off (`Deterministic`).

### Long-range rocket truck (2026-10-26)

The owner's spec: a Resistance launcher of a generic 220/302 mm heavy rocket type, `longrange_rocket_truck`, built at the
vehicle garage. Look (for the client): a cab-over light truck with a flatbed carrying an open steel lattice cage of 10 tubes
in 2 rows of 5, raised steeply on hydraulic rams, with stabiliser legs at the rear. Content only (commit 9ab86c5); decisions
488 to 497.

- **Weapon `longrange_rockets`.** 300 damage a rocket (the heavy rocket warhead: 600 to buildings, 360 to armour), a 5-cell
  splash from 100% at the centre to 10% at the edge, scatter growing with the distance from 4 cells at 50 to 6 at 130 (an
  unguided rocket), the usual launcher arc, 18 cells/s, `interceptible: rocket`. Range 50 to 130 cells (`minRange` 500,
  `range` 1300): the second-longest ground missile, below the tactical missile's 160 and above the twin missile's 100.
- **Ripple.** 10 rockets, one every 1.5 s (30 ticks), after the 6 s deploy. No single-shot mode (decision 494).
- **Reload.** Only at a supply depot and only stowed: 20 s and 80 supply a rocket (a full 10 is 80% of a depot), 1 s after
  the last shot; an idle deployed truck at a depot packs up first, as the twin missile truck does.
- **Fires only on orders** at ground points or seen units, like the other launchers; an attack on a unit needs a spotter, an
  attack on a ground point does not.
- **The truck.** 1,600 supply, 50 s to build, 280 HP, `medium_vehicle` armour, wheeled, speed 16 (the heavy rocket truck's),
  deploy 6 s, stow 5 s, tags `vehicle`, `launcher`, `heavy`, a driver like the other trucks, glyph `X`. In the Resistance
  list only; the golden battle's armies are unchanged.
- **Tests.** `LongRangeRocketTests`.

### Hypersonic missile truck (2026-10-27)

The owner's spec: the Resistance's hypersonic missile truck, the most powerful unit in the game, `hypersonic_truck`, built at
the vehicle garage, **one a player at a time**. Look (for the client): a heavy 8x8 truck with two big box launch tubes side by
side that raise to the vertical when it sets up; the missile is 12 m long, a finned body with a pointed manoeuvring nose.
Content: `archetypes/vehicles.json` (`hypersonic_truck`), `components/vehicle_weapons.json` (`hypersonic_missiles`),
`air_classes.json` (`hypersonic`), `exclusive_groups.json` (`hypersonic`), its line in `factions/resistance.json`, and the
class added to the Patriot and THAAD types (`components/sentinel.json`). Engine: the weapon `terminal` block and the
weapon `fire` block (generic, opt-in; `Sim/Systems/TerminalSystem.cs`). Tests `HypersonicTests` (27 tests, 37 cases). Decisions 515 to 532; the arc and the interception
review (H4 and HYP-INT, 2026-10-28) 591 to 600.

- **The truck.** 4,000 supply, 90 s to build, 500 HP, `medium_vehicle` armour, heavy wheeled (radius 1.3 cells: it pushes trees
  over), speed 14 (1.4 cells/s), set up 10 s, stow 8 s, tags `vehicle`, `launcher`, `heavy`, a driver like the other trucks,
  glyph `\`. **The cap:** archetype `exclusive: "hypersonic"`, the group's `max` 1 without a type lock: production refuses a
  second while one is alive, queued or being built (`World.ExclusiveOf`, `ExclusiveRefusal.AtMax`); a lost one can be rebuilt.
- **Range and orders.** 150 to 1,500 cells (`minRange` 1500, `range` 15000): the longest range in the game. **No spotter**:
  it fires at any ground point it is ordered to, seen or not (the launchers' rule for ground points); an attack on a unit
  needs the unit seen, as for every launcher. **Two missiles, 4 s apart**: a single-shot weapon with 2 rounds and an 80-tick
  reload, so a Shift-queued second ground point (`Unit.GroundQueue`) takes the second missile; with one point both go there.
- **The flight** (weapon `terminal`; a closed form of the missile's age, so the sim and the renderer agree to the raw unit; H4,
  2026-10-28: one smooth arc instead of the climb, level cruise and straight dive, which the owner found read as straight
  lines). Along the straight line from the truck to its point at **125 cells/s over the ground** (`projectileSpeed` 1250), its
  height a function of the way along, its slope continuous everywhere:
  - **the rise**: off the truck steeply but tilted (a cubic easing out, three times the rise over its length: about 52 degrees
    on a 1,500-cell shot, 57 at 800, 77 at 200) up to its **peak a fifth of the way** to the dive's start (`peakAt` 20), level
    there. The peak is **a tenth of the ground range** over the higher of the launcher and its point (`peak` 10), **at least
    100 m and at most 250 m** (`peakMin` 1000, `peakMax` 2500): 100 m at 200 cells, 160 m at 800, 250 m (the cap) at 1,500.
    The first 1.5 s are the `Climb` phase (`climbTicks` 30), the rest of the arc `Cruise`;
  - **the fall**: a parabola from the peak down to the **separation point**, 30 cells from its point and 72 m over it (`from`
    300, `slope` 1200: the dive's chord, 50 degrees), about 8 degrees down when it gets there;
  - **the terminal dive**: over the last **30 cells** at **210 cells/s** (`speed` 2100), a parabola from the fall's slope onto
    its point, ending at about 66 degrees, that **weaves** across its line, a swing of up to 1.5 cells in one wave (`weave` 15,
    `weaves` 1), 50 to 100% of it and its side seeded per shot from the match seed and the projectile ID (no RNG draw), back on
    the line at the impact.
  The fastest projectile: never under 125 cells/s in space (above every interceptor, the THAAD type's 120), 210 in the dive
  (above every other projectile, the gunship's light cannon, 200). Flight times, launch to impact: 1,500 cells 238 ticks (11.9
  s; 268 before H4), 800 cells 127 (6.35 s; 157), 200 cells 31 (1.55 s; 61), 150 cells 23 (53): the old climb's 1.5 s shorter
  at every range. On terrain a hill in its way stops it; in its last 2 cells it hits the first unit or structure under it (a
  lobbed round).
- **Damage.** 1,400 a missile on the `ballistic` warhead's table (3,080 to a building, 2,380 to armour before a tank's top
  armour: 3,570 to an Abrams, which dies in the inner ring), full within 4 cells and a flat 30% out to 10 (`splashInner`,
  `splashOuterPercent`), scattered up to 1 cell per axis, a large runway crater; it fells trees as the ballistic missile does.
- **Fire.** The weapon's own `fire` block (the warhead fire block's fields, on top of the warhead's; generic): radius 4 cells,
  every forest cell inside it, spreading at the fire rules' normal chances (`radius` 40, `spreads` true).
- **Interception.** A new air class `hypersonic` (appended: value 20). Short-range air defence (the Igla, the Osa type, the
  SA-6 type) never lists it. The Patriot type (`killBest` 9) and the THAAD type (`killBest` 17), `speedRefs` 1400 for both, no
  salvo: one at a time. **Only before the terminal dive**: once it dives no interceptor picks it, and the missiles already at
  it lose it and fly on to self-destruct (`World.Evades`). Measured on the H4 arc with a battery and its radar at the target
  (30 seeds x 2 missiles at 380, 700 and 1,200 cells): the Patriot type (`killBest` 6 since H4, 9 before) gets a shot at 50 to
  58% of the missiles, 5.0% a fuse (5 kills in 95 fuses); the THAAD type at 55 to 72%, 12.8 to 13.4% a fuse, and now often a
  second shot after a miss (its cap of 2: 212 fuses at 114 missiles, 31 kills; before H4 one shot each, 15.4%, 14 kills in
  103). A 200-cell shot (1.55 s) is launched at but never reached. The climb can be engaged (only next to the truck).
  **Only what it defends** (`defendRadius` 150, every arcing threat's rule): a battery engages it only when its point is within
  15 cells of a unit or structure of the battery's side; a battery on its path short of an empty point lets it land
  (`TheDemoBatteryDefendsOnlyWhatIsNearThePoint`: the client demo's battery fired at none of truck B's missiles on seeds 2
  to 5 and 42, and at both on every seed with one rifleman at the point).
- **Counterplay.** A **5 s incoming warning** (`warn` 100) for every player but the shooter's (the engine has no teams): the
  impact point, the blast radius, the impact tick (`World.IncomingStrikes`, rebuilt from the missiles in flight; a flight
  shorter than 5 s is warned at its launch). **Each launch shows the truck to every enemy for 20 s** (weapon `reveal` 400, the
  howitzer's flash, generic).
- **Reload.** Only at a supply depot, only stowed: 90 s and 400 supply a missile (`depotReload`), 1 s after the last shot; an
  idle set-up truck at a depot packs up first.
- **For renderers.** `World.FlightPointAt(p, tick, permille)`: the missile's position (the weave in it), height and phase at
  any tick, now or ahead, and between ticks (thousandths), on the curve the sim steps through: draw the arc and the weaving
  dive from it (the `Climb` phase is no longer straight up: it moves along its line from the first tick). `World.ImpactTickOf(p)`, `World.TerminalPhaseOf(p)` (`Climb`, `Cruise`, `Dive`).
  `World.IncomingStrikes` (`IncomingStrike`: projectile, owner, shooter, point, radius, impact tick, the warned players'
  bits): the warning marker and countdown. `World.IncomingEvents` each tick (`Launched`, `Warned`, `Diving`, `Struck`,
  `Intercepted` with where it came down). The reveal: `World.RevealedBy(truck, player)`, `RevealLeft`, `EmplacementEvents`
  (`Fired`, `Revealed`). The fire: `FireEvents` (`Ignited`, spreading). The blast: `Detonations`, `Craters`.
- **Hashing.** A terminal-profile missile adds its age, weave seed and height (`World.Hash` and `HashReference`); its arc's
  origin and heights are hashed like any arcing projectile's (its peak's absolute height is in `ArcRise`, H4). Nothing else is
  new state.

### S-400 type air defence (2026-10-27)

The Resistance's long-range wall against aircraft and the only air defence that sees the stealth bomber well (the owner's spec
of 2026-10-27, "S-400 type long-range air defence system", game repo; the owner approved the work that day). A battery of parts:
one radar vehicle and launchers. Content: `archetypes/vehicles.json` (`s400_radar`, `s400_launcher`),
`components/vehicle_weapons.json` (`s400_missile`), their line in `factions/resistance.json`. Engine: three generic, opt-in air
sensor fields (`counterStealth`, `radarHorizon`, `emitter`; `Sim/Systems/LongRangeRadarSystem.cs`) and two small extensions
(an interceptor launch can flash, `reveal`; an emitter takes a stance). Everything else is the SA-6 battery, the mobile radar
and the stealth multirole's radar spotting, reused. Tests `S400Tests` (14 tests, 32 cases). Numbers marked (spec) are the
spec's starting values; the rest are rows 551 to 569 of "Decisions to confirm". It is one of the owner's tier 2 air defence
picks (`doctrine-picks.md`), but picks are not built: the battery is in the Resistance list like any unit, with no limit (spec).

- **The parts.** The radar (`s400_radar`: a big flat array on a mast over an 8x8 truck) and the launcher (`s400_launcher`: four
  tall canisters raised vertical on an 8x8 truck), both wheeled, `medium_vehicle` armour, 2.5 cells/s, **set up 30 s, packed
  up 20 s** (spec), a driver each. Radar **320 HP, 3,000, 60 s**; launcher **340 HP, 2,500 with its missiles (2,100 + 400),
  55 s** (spec), both from the vehicle garage, one part an order. Any move packs a part up first (the deploy rule); a part
  searches or fires only set up.
- **Seeing: 800 cells (spec).** The radar's `airVision` is 800 cells of slant from its antenna **25 m up** (`height` 250),
  by each target's signature, its array turning once every **4 s** (`sweepTicks` 80, `World.RadarSweep`). Its tracks are its
  side's (team cueing, unchanged) and its `cueLock` 70 cuts a friendly ground shooter's lock to 70% (the mobile radar's rule).
  Measured with each Sentinel aircraft at its own height (`EachSentinelAircraftIsDetectedAtRange`): tracked at 96% of 800 x
  its countered detect percent and not at 104%; normal signatures out to 800 cells (the drones, the helicopters at 30 m, the
  jets, the bombers, the airlifter), the air superiority jet to 600, the stealth multirole to 536, the stealth bomber to 496.
- **Counter-stealth (spec: `counterStealth` 50).** Against this sensor every value of a target's signature moves 50% of the
  way to 100 (integer, truncated): the stealth bomber's detect 25 / lock 300 / kill 70 count as **62 / 200 / 85**, so it is
  seen out to about **496 cells of slant** instead of 200 (measured: tracked at 480 and 490, not at 505 and 520, 700 m up;
  `TheStealthBomberIsSeenAtAboutFiveHundredCells`). Other sensors keep the normal signature (the mobile radar beside it never
  tracks the bomber). It counts wherever this sensor detects: the side's tracks, the fog, the battery link and the
  semi-active guidance. The launchers lock and kill by the signature their radar sees: a battery's counter-stealth is the
  highest among the archetypes carrying its radar tags (content only), so the launcher needs no number of its own (decision
  553). Measured: 3 s x lock 200 x the radar's cue 70% = **84 ticks** of lock on the stealth bomber before launch
  (`TheLauncherLocksTheStealthBomberByTheCounteredSignature`). This replaces decision 194 for this one sensor (row 568).
- **Radar horizon (spec: beyond 200 cells, below 50 m).** Beyond 200 cells (level distance) it does not see an air target
  flying lower than 50 m over the ground under it while the ground cuts the straight line from its antenna to the target
  (the terrain's `LineOfSight`, a sample every half cell). Measured with a 40 m ridge 50 cells out: an attack helicopter 30 m
  up behind it 150 cells out is tracked, one 250 out is not (`BelowRadarHorizon` true), a jet 200 m over it is; on a flat map
  both helicopters are tracked, and the mobile radar sees the far one through the ridge as before
  (`ALowHelicopterBehindTheGroundIsUnseenBeyondTwoHundredCells`). Every other sensor still sees air targets through ridges.
- **Firing (spec).** `s400_missile`: **4** a launcher, **one every 3 s**, **20 to 600 cells** of slant, **100 cells/s**, guided
  for **700 cells**, never at a target lower than **20 m** over the ground (`minAltitude`), lock **3 s** x the target's lock
  value (after counter-stealth; the radar's cue applies, decision 554). Best kill chance **85** against jets and bombers, **70**
  against helicopters, then aspect, speed and range as the SA-6's, then signature and flares (decision 559). **Bombers lose 35%
  of their HP a hit, jets die to one** (decision 560). **2 missiles at a bomber, 1 at anything else** (`salvo`; measured with
  two launchers: at most 2 in flight at a strategic bomber, 1 at a multirole, `TwoMissilesFlyAtABomberAndOneAtAJet`).
  **Aircraft only**: fixed-wing and helicopters; not missiles, rockets or drones (the Watcher, the Overseer, the scout, the FPV,
  the Kaman; decision 552).
- **The battery (spec: the SA-6's link, wider).** The interceptor's `battery` block: `linkRadius` **100 cells**, `radarTags`
  `s400_radar`, `semiActive`. A launcher fires only at what a set-up S-400 type radar of its side within 100 cells tracks
  itself; any number of launchers join (decision 551). Measured: with a radar 110+ cells off and the SA-6's mobile radar
  beside it, set up, it holds fire with its side tracking the jet; the radar 20 cells off set up, it fires
  (`ALauncherFiresOnlyWithALiveSetUpRadarWithinOneHundredCells`). The SA-6 launcher does not link to it nor it to the mobile
  radar (decision 562). **Semi-active**: the tick no radar linked to its launch point tracks the target (destroyed, packed up,
  hidden, the target out of reach or below its horizon) the missile loses its guidance for good and misses (measured: lost the
  very tick, then a self-destruct; `MissilesMissWhenTheRadarDiesOrPacksUp`).
- **Giving itself away (spec: `emitter` 800 cells).** While the radar searches, on each sweep (the tick its array passes north,
  every 4 s) every working enemy unit with an air vision or a ground radar whose sensor is within 800 cells (slant from the
  antenna) and that is not infantry (the soldiers' eyes) reveals it to its side: the radar and a **2-cell** disc round it,
  through the fog, for **10 s** after the sweep (the stealth multirole's `RadarSpot`, renewed every sweep, so it stays on while
  it searches). Measured: a Sentinel radar truck 700 cells off reveals it, one 860 off and a Humvee 100 off do not; packed up,
  the reveal ends within 10 s (`TheEmitterRevealsTheSearchingRadar`); a multirole 600 cells out reveals it, a rifleman 50 cells
  off does not (`AnEnemyJetsRadarPicksItUpAndAnEnemySoldierDoesNot`). The stealth multirole's own radar spotting reveals it too
  (tag `air_radar`). **Each launch reveals that launcher for 20 s** (weapon `reveal` 400, the hypersonic truck's; it now works on
  interceptor launches too). **Hide** (or Stow) switches the radar off: it stops searching, cueing, guiding and emitting (the
  reveal ends after its 10 s linger) and blinds the battery (`TheHideStanceBlindsTheBattery`; an emitter takes a stance with no
  weapon, decision 558).
- **Reload (spec).** Only at a supply depot, packed up: one missile every **30 s** for **400** supply, 1 s after the last shot
  (`depotReload`), the SA-6's rules.
- **For renderers.** `World.RadarOf(radar)` (`On`, `Air` 800 cells, `CueLock` 70, and the new `CounterStealth` 50, `Horizon`
  (the `RadarHorizon`: `BeyondRaw`, `BelowRaw`: the gap in the coverage ring), `EmitterRange` 800); `World.RadarSearching(radar)`
  (the search state: the array turns, it tracks, guides and gives itself away); `World.RadarSweep` (the array's bearing, -1 off);
  `World.RadarTracks(radar)`; `World.SignatureSeenBy(sensor, target)` and `World.CounterStealthOf(def)`;
  `World.BelowRadarHorizon(radar, target)` (a target slipping under it); the reveal: `World.RadarSpots` / `RadarSpotOf(radar,
  player)` (the marker the Sentinel sees: `Spotter` is now an aircraft or the enemy sensor that picked it up, `Since`, `Until`,
  `Inside` true on the sweep that renewed it, `Radius`); the battery: `World.BatteryRadar(launcher)`,
  `BatteryLaunchers(radar)` (the link lines), `BatteryOf(def)` (`LinkRadiusRaw`: the 100-cell circle), `BatteryLost`; the launch
  flash: `World.RevealedBy(launcher, player)`, `RevealLeft`, `EmplacementEvents` (`Fired`, `Revealed`); orders `Deploy`, `Stow`,
  `SetStance` (Hide). No new `CommandKind`.
- **Hashing.** No new state: the reveals are `RadarSpot`s (hashed in `World.Hash` and `HashReference` already); the horizon
  checks and each battery's counter-stealth are scratch worked out from hashed state and content. The content hash covers the
  new archetype fields only when set. Deterministic over two runs (`Deterministic`).

### Cruise missile truck (2026-10-28)

The Resistance's sneak strike (the owner's spec of 2026-10-27, "Cruise missile truck", game repo; approved for the engine on
2026-10-28, a Resistance unit): a truck fires slow cruise missiles that fly low along routes the player draws, hugging the
ground behind ridges and forests to stay under the enemy's sensors. Content: `archetypes/vehicles.json` (`cruise_truck`),
`components/vehicle_weapons.json` (`cruise_missile`), `air_classes.json` (`cruise`), its line in `factions/resistance.json`, and
the class added to the Patriot and THAAD types (`components/sentinel.json`), the air defence vehicle and the Igla. Engine:
`Command.Route` on an attack-ground order, the weapon `cruise` block (the route and the terrain-following flight), the weapon
`lowFlight` block (the low-flight sight rule), the weapon `signature`, the archetype `deploySlope`; all generic and opt-in
(`Sim/Systems/CruiseSystem.cs`). Tests `CruiseTests` (16 tests, 19 cases). Numbers marked (spec) are the spec's starting values;
the rest are rows 570 to 590 of "Decisions to confirm".

- **The truck (spec).** 420 HP, `medium_vehicle` armour, 1.6 cells/s, set up 8 s, stow 6 s, 3,500 supply with its missiles, 80
  s at the vehicle garage. The hypersonic truck's heavy wheeled chassis (radius 1.3 cells: it pushes trees over), a driver,
  tags `vehicle`, `launcher`, `heavy`; no build limit (spec). **4 missiles, one every 2 s** (`ammo` 4, `reload` 40). Depot
  reload only stowed: **60 s and 300 supply a missile** (`depotReload`), 1 s after the last shot. **Each launch shows the truck
  to every enemy for 20 s** (weapon `reveal` 400, the hypersonic's).
- **The route (spec).** `AttackGround` with `Command.Route`: up to **8 waypoints** (whole cells) and the target (`Cell`); one
  route a missile: Shift (`Queue`) adds the next missile's route, so one launch sends 4 missiles on 4 routes
  (`Unit.CruiseRoutes`, one taken per missile in order; the order ends with the last). The route's length is its straight legs
  from the truck through the waypoints to the target: **150 to 2,000 cells** (`routeMin`, `routeMax`), else the order is refused
  (`OrderRefusals`: `RouteInvalid`). The target is a ground point; no spotter (the launchers' rule for points). The weapon's range
  is 2,000 cells so the truck never drives to fire; it never fires at a unit (an Attack order, auto targets).
- **The flight (spec; worked out at launch).** The boost: **40 m above the launch point in 2 s**, straight up (`boostTicks` 40,
  `boost` 400). Then the path: the straight legs with each waypoint rounded by an arc of **6 cells** tangent to both legs (a
  tighter one where a leg is shorter than twice the tangent; measured: 2.5 cells from a right-angle corner at the nearest tick,
  1.76 on the arc), at **35 cells/s** (`projectileSpeed` 350: a 1,000-cell route about 30.6 s with the boost). The height,
  tick by tick along the path: **15 m over the highest ground and treetops within 10 cells ahead** (`height` 150, `lookAhead`
  100; the fog's canopy per forest density, sampled every half cell); a backward pass starts each climb early enough to make it at
  **20 m/s** (`climbRate` 200: 1 m a tick), a forward pass from the top of the boost descends at most **15 m/s** (`descendRate`
  150), so over a slope too steep to follow it stays higher. Over the last **4 cells** (`diveFrom` 40) a straight dive onto its
  point from a **pop-up to 40 m** over the ground there (`popUp` 400; the climb to it is within the 20 m/s). Measured over a map
  of rolling hills to 25 m with groves (`TerrainClearanceOverTheHillsWithinTheClimbAndDescentLimits`, 569 cells of route): 15.0 m
  over the ground at the lowest, 20.9 m on average, climbing at most 20 m/s and descending at most 15 m/s, never under the ground
  at a tick or between ticks; the sim flies exactly what `FlightPointAt` drew at the launch.
- **Legs it cannot clear (spec).** A route whose planned path meets the ground at those rates (a wall right off the truck: only
  there can a climb not start early enough) is refused (`RouteTerrain`); `World.CheckCruiseRoute(truck, waypoints, target)`
  names the first leg that fails (0 from the truck) for the HUD's red leg, and the length (`ALegItCannotClearIsRefusedAndNamed`).
  At the launch the plan is made again from where the truck stands; a plan that meets the ground (the terrain changed, or the
  truck moved to a spot to set up) goes off there. In its last 2 cells it hits the first unit or structure under it.
- **Being seen (spec).** Signature **detect 40, lock 150, kill 100** (weapon `signature`: a radar truck's 360 cells see it at
  144). The **low-flight rule** (weapon `lowFlight`: `below` 300, `sight` 1200): while it flies **lower than 30 m** over the
  ground under it, a sensor that is not an aircraft (a soldier's eyes, a vehicle's or a structure's radar) sees it only **within
  120 cells** (level) and with a **clear straight line from its eye over the terrain and the treetops** (`World.ClearSight`); an
  aircraft's sensor looks down and ignores it; higher than 30 m, normal sensing. It counts wherever sensing counts: its side's
  tracks (team cueing), the interceptors' own sight, the radar-linked and battery launchers. Measured: a 40 m ridge or a band of
  dense forest between it and the sensor hides it, the open does not (`ARidgeHidesItFromARadarAndTheOpenDoesNot`,
  `AForestHidesItFromASoldier`); a jet over it sees it behind the ridge (`AnAircraftSensorIgnoresTheLowFlightRule`). **Exposure:**
  from a 60 m hilltop the missile stays more than 30 m over the falling ground for about 160 cells and a radar 130 cells off its
  path sees it; from a valley floor it is low after about 23 cells and the same radar never sees it
  (`AHilltopLaunchIsSeenAndAValleyLaunchIsHidden`). The boost is always above 30 m: a sensor within its normal reach of the
  launch point (144 cells for a radar truck) sees every launch (decision 579).
- **Warning (spec).** Unseen it gives no warning (`Launched` carries no warned players). Once an enemy player's sensors see it
  he is warned (`IncomingStrikes`, only his bit in `Warned`) while they see it and **5 s after** they last did (`warn` 100), and
  `IncomingEvents` says `Seen` (new, appended) on his first sighting; the track is his side's (team cueing).
- **Being shot down (spec, a new class).** Air class `cruise` (appended: value 21; decision 574): the Patriot type **best 85**
  (its `missile` number; speed reference 40 cells/s so its 35 do not cut it), only on its side's radar tracks and only one whose
  point is within 15 cells of its side (`defendRadius`, as for an arcing missile); the air defence
  vehicle and the Igla treat it as a drone (no class terms: their best and their 12 cells/s speed reference, so about a third of
  their chance at 35 cells/s). Measured: a Patriot type with its radar truck brings down 5 of 6 that fly at it in the open, after
  its radar sees it (`APatriotKillsOneItSees`). **THAAD** lists it but its 30 m `minAltitude` holds it off while it flies low:
  none launched (`TheThaadTypeNeverEngagesItLow`). **Fighters do not engage it** (no air-to-air weapon lists the class; owner's
  default, backlog). Not the SA-6 or S-400 types (aircraft only).
- **The warhead (spec).** **900** on the `heavy_missile` table (1,980 to a building), full within 3 cells and **30% out to 8**,
  no scatter, a large runway crater; **no fire**.
- **The slope rule (spec: 15%, this truck only).** Archetype `deploySlope` 15: it sets up only where no cell under its footprint
  (every cell the square of its radius touches) has a grade above 15% (`TerrainMap.Grade`); a Deploy order on steeper ground is
  refused with `OrderRefusalReason.Slope` and the truck stays where it is. An attack-ground order on such ground drives it to a
  spot within 4 cells first (the launchers' old rule, with this one in it). Every other launcher keeps only its movement class's
  grade rule on its own cell (`DeployIsRefusedOnASteepSlopeForThisTruckOnly`: on a 12% cell beside 18% ones it is refused, the
  hypersonic truck sets up). `World.FootprintGrade(def, cell)` and `World.DeploySlopeOk(def, cell)`: the HUD's tint.
- **For renderers.** `World.CheckCruiseRoute(truck, waypoints, target)` (`CruiseRouteCheck`: `Ok`, `Problem` (`TooManyWaypoints`,
  `OffMap`, `TooShort`, `TooLong`, `Terrain`), `Leg`, `Length`) and `World.RouteLength`: the HUD's length readout and red leg.
  `World.FlightPointAt(p, tick, permille)` (now also for a cruise missile: its position and height at any tick from the plan,
  between ticks on the straight line; phase `Climb` in the boost, `Dive` in the final dive), `ImpactTickOf`, `TerminalPhaseOf`.
  `Projectile.Cruise` (`CruiseState`: `Route`, `BoostTicks`, `DiveAge`, `ImpactAge`, `Seen`, `SeenNow`, `Warn`);
  `World.CruiseWarned(p)`, `IncomingStrikes`, `IncomingEvents` (`Launched`, `Seen`, `Struck`, `Intercepted`); the reveal:
  `World.RevealedBy(truck, player)`, `RevealLeft`, `EmplacementEvents`. The slope: `FootprintGrade`, `DeploySlopeOk`,
  `OrderRefusals` (`Slope`, `RouteInvalid`, `RouteTerrain`). No new `CommandKind`: the route rides on `AttackGround` (replays
  write `route` only when set).
- **Hashing.** A cruise missile adds its age, height, the digest of its route and planned flight, who has seen it and the
  warnings (`World.Hash` and `HashReference`); a truck its routes still to fly. Deterministic over two runs and the route is
  hashed (`Deterministic`). Cost: the 16 missiles of 4 trucks over hills into a Sentinel army of 300 with its sensors and air
  defence add 0.023 ms to the tick p99 and 0.013 ms to the mean against the same world with the trucks idle (`SimRun
  -bench-cruise 4`, Release, M3 Pro, `bench/cruise_4.json`); a flight plan at launch is about 0.05 ms for a 600-cell route.

## Sentinel faction

The second faction (2026-10-19; content `content/mvp/{archetypes,components,factions}/sentinel.json`, lobby `content/setups/sentinel.json`, `Sim/Systems/WatcherSystem.cs`, tests `WatcherTests`). **Sentinel** is the regular army of a fictional country with modern real-world gear (no real flags, insignia or text). Where the Resistance has numbers, tunnels and cheap drones, the Sentinel has an expensive, precise strike drone that air defence must answer. The full design and its tuning reasons live in the game repo's `docs/watcher-design.md`.

**Roster.** To be playable now it fields the shared basics (the same archetypes as the Resistance: riflemen with the assault soldier's mines, anti-armour and air defence soldiers, workers, the air defence vehicle, the supply depot and the garage; its own cargo truck replaces the shared one and, since 2026-10-24, its **Humvee** the technical: "Humvee" below), and its own units so far: the **Watcher** strike drone and the **Overseer** surveillance drone, each with its own **control station** (one drone type per station). Presets: Rifleman, Gunner, Raider, Tank hunter, Drone hunter. Its first tank, the Abrams, came on 2026-10-24 ("Main battle tank" below), and the Booker light tank the same night ("Light tank").

**The Watcher** (`watcher_drone`, an MQ-9-type drone). It flies like the scout drone (a `flight` block): 150 m up, 8 cells/s, orbiting the point it is sent to 25 cells out, 20 minutes of endurance, seeing 35 cells round the point under it. It carries **2 missiles**, fired only on an attack order at an enemy ground unit it sees within 35 cells of the point under it (it circles over a target out of reach, then goes back to its task: since E3 its orbit point stays where it was). The hit is rolled once at launch: **100%** on a target that stood still last tick, **80%** on one that moved; a miss lands 4 to 7 cells off, behind a moving target. A hit destroys soldiers and vehicles outright, except vehicles tagged `heavy` (the 30-tube rocket truck, the heavy rocket, twin missile and ballistic trucks, the infantry carrier), which a second roll destroys 80% of the time and otherwise leaves at 25% HP; a building takes 600 through the `air_strike` warhead (720); then the splash. Only air defence reaches it: the air defence vehicle at best 40% a missile, the AA soldier 10% (the `watcher` interception class), between the FPV drone (easy) and the scout drone (hardest).

**The control stations: one drone type per station** (2026-10-19). The **Watcher control station** (`watcher_station`, 12 x 8 cells, 1200 HP, 45 s to build, 1500) holds one Watcher and nothing else (`watcher_launch`, its only launch ability), one aloft at a time; the **Overseer control station** (`overseer_station`, the same footprint, HP, build time and cost as night defaults) holds one Overseer the same way (`overseer_launch`). Every station archetype carries exactly one launch ability (`ContentTests.EveryStationLaunchesOneDroneType`); a launch order naming the other kind's ability does nothing. Both are in the Sentinel faction's list and its lobby's starting army. For the Watcher: Launched at a point, the drone climbs and orbits there; short of endurance (never because it is empty: see "Air combat", the owner's rules) it flies home, circles down and is recovered only once low over the apron; after a 30 s turnaround the station relaunches it to the orbit it left with a full tank and the missiles and HP it came home with (both missiles and full HP after a Rearm order). A Watcher shot down is replaced only beside a supply depot (60 s, 800 supply). With its station gone the drone orbits until it runs dry.

**The Overseer** (`overseer_drone`, a Heron Mk II-type surveillance drone at 0.85 scale; the game repo's `docs/overseer-design.md`; tests `OverseerTests`). **Unarmed**: it watches and spots. Content only, on the Watcher's flight model (a `flight` block): **250 m** up (the Watcher 150), **6 cells/s** (8), orbiting 30 cells out (25), **40 minutes** of endurance (20), seeing **50 cells** round the point under it (35; over ridges and woods like any air unit). 150 HP. Its own Overseer control station launches it (`overseer_launch`, that station's only launch: one aloft at a time); it comes home short of endurance, is recovered low over the apron, refuelled for 30 s and relaunched to where it orbited (with the HP it came home with; a Rearm order repairs it); one lost is replaced only beside a supply depot (45 s, 600 supply). The station remembers which drone came home (`Unit.RelaunchArchetype`, hashed with the pending relaunch) and relaunches that kind (generic: with one drone type per station it is always the station's own); a second drone home while one relaunch waits stays in the station for its next order. Its `aircraft` block is class S like the Watcher's (span 14.1 m, length 6.8 m, 30 cells to land, 20 to take off, slower approach and takeoff speeds), so it holds, lands, parks, refuels and sorties at an airfield exactly like the Watcher (M2). *Air defence* engages it as the `overseer` interception class, with a **hit rule** instead of the kill model (`intercept.hits`, generic per class: a flat hit chance and the share of max HP a hit takes; 100 or more kills; a projectile target is always killed by a hit): the AA soldier's missile hits **45%** and takes **55%** (82 of 150: two hits bring it down; `AirBurstKind.Hit` reports a hit it survives), the air defence vehicle's hits **65%** and kills. Its height does the rest: at 250 m (125 cells) the soldier's 140-cell slant range reaches it only within about **63 cells** of the point under it (the vehicle's 220 cells: about 180). Rifles, guns and every other weapon cannot reach it (no flying unit is a target for them). The Watcher's and the attack drones' outcomes are unchanged (no hit rule for their classes).

**The bulldozer** (`bulldozer`, a D7-type tracked dozer; placeholder numbers): the Sentinel's builder vehicle, a worker on tracks. It carries the worker's builder at the worker's rate with a cell more reach (`dozer_build`: a vehicle keeps a cell clear of a footprint), so it builds everything a worker builds, the airfield pieces included, and repairs what builders repair (M3a: damaged and fallen airfield pieces). No crew, no weapons; light armour, 300 HP, speed 20 (worker 18, cargo truck 22); from the vehicle garage (20 s, 400).

**The military garage and the Sentinel cargo truck** (2026-10-21; the game repo's `docs/military-garage-design.md`; `Sim/Systems/ServiceSystem.cs`, `MilitaryGarageTests`; placeholder numbers). The **military garage** (`military_garage`) is a long arched hangar, 40 x 18 cells (twice the vehicle garage), 4000 HP, built by a worker or the bulldozer in 60 s for 4000, with 5 doors along its south front. It is a factory like the vehicle garage (any vehicle of the faction, 10 orders, the same doors, exit lanes, step-aside and rally), and a content `service` block makes its doors **shared bays**: a production bay takes a free door when it takes an order and builds behind it, at most 3 doors producing at once (finished vehicles still coming out count), and every other door services a vehicle, so 5 are serviced while nothing builds, 2 while 3 build. A door freed in a tick goes to production first (production runs before service).
- *Service* (`CommandKind.Service`, the garage as target): only its side's ground vehicles with a tag the service lists (`vehicle`; never aircraft, drones, infantry or structures) and a driver. A vehicle drives toward the nearest door and waits within 8 cells of a door's exit; free doors go to the waiting vehicles in the order they got there, then by ID, each the free door nearest to it (then the lowest). It drives to that door's exit point, the door opens (1.5 s), it drives straight in to the bay floor (its garage's footprint doesn't block it, as a vehicle driving out), and in its bay, in parallel: every weapon is refilled after 5 s, HP rises evenly to full over 10 s (from what it had: from + (max - from) x k / 200 after k ticks), an empty cargo truck is refilled after 10 s. With all its needs done it drives out through the same door to the garage's rally point (as a produced vehicle does), and the door closes once it has left the exit lane. Nothing is charged until there is an economy. The reload pad works as before.
- *In the bay* (decision): a vehicle is off the map as a unit underground is (the existing `Unit.Inside` rule with the `World.InGarage` marker): nothing sees, targets, hits or pushes it, it neither sees nor fires and takes no orders, like a vehicle still being built in a bay; driving in and out it is an ordinary unit. The client draws it in its bay from the door (`Serviced`) and shows `World.ServiceProgressOf`.
- *Stops:* a garage that is destroyed, unfinished, rubble or below 35% HP (production's `minHpPercent`: the doors are wreckage) services nothing: a vehicle inside or driving in comes out to the rally point, the others' orders end (no queue is kept), new orders are ignored. Another order (or Stop) while a vehicle drives in sends it back out through its door (Stop: to the rally point); before it reaches its door the order just ends and the door is free again; one that can't reach its door gives it up.
- *Choices made:* out to the rally point (not back where it came from); the waiting distance (8 cells) and the "at the door" distance (2 cells) are code constants like the exit lane's; a vehicle that needs nothing goes in and comes straight out.

**The Sentinel cargo truck** (`sentinel_cargo_truck`, a modern armoured 8 x 8 hook-lift truck with a 20 ft container): the cargo truck's `cargo_load` (400 supply, unloaded into a friendly depot all at once after 3 s), a bit tougher (260 HP against 200) and as fast (22). It replaces the cargo truck in the Sentinel's roster and lobby (the Resistance keeps its own); built in either garage (15 s, 300); refilled only by garage service (10 s), so far the only refill there is.

**Airfield (M1, 2026-10-19; the game repo's `docs/airfield-design.md`).** The Sentinel builds a compact **forward airfield**, piece by piece on a grid (content: `airfields.json`, `archetypes/airfield.json`). Workers build the **facility** (10 x 10 cells, 2000 HP, 45 s), which founds it and reserves the first block (3 runway segments and the first row) against every player's buildings; it needs flat-enough ground (within 4 m of one pad height) and 30 cells of approach corridor on the map beyond each runway end. With the facility standing, **rows** (28 x 10: a loading shelter, an 8-cell lane slice and an open stand; 20 s) grow the field: a row touches the field and has the row below it, its lane mouth over the parallel taxiway. The first row also lays the first 3 **runway segments** (20 x 24: runway 10, grass 6 with a connector, taxiway 8) on its one bar (65 s); segments extend either end. A finished row spawns its **loading station** (shelter: 1000 HP, rearm and repair 2x, only while the facility stands) and its **open stand** (1x). Rows, segments and stands are pavement (ground units drive across), never targets and never damaged. The field turns in quarter turns with the facility. Aircraft carry a generic `aircraft` block (the Watcher: class S, span 14.1 m, length 7.9 m after the 0.7 scale, 36 cells to land, 24 to take off); the forward airfield serves classes S and M, and a test fixture's class-M jet proves nothing is Watcher-specific. The weapons' runway effects (craters and wear) are data already. Not yet: landing, taxi and takeoff (M2), service, targeting rules beyond "only the facility and shelters are targets", rubble, repair and craters (M3).

**Airfield (M2: landing, taxi, takeoff, 2026-10-19).** An aircraft (a flying archetype with a flight block and a runway `aircraft` block) whose home is an airfield keeps its ID and HP there; nothing in the rules is specific to the Watcher. *Home:* the nearest of the owner's airfields (to its hold point; ties by ID) whose type serves the aircraft's class, whose runway has an open stretch of finished segments at least its `minLandRun` long holding a connector (and its `minTakeoffRun` from the westmost connector to the east end), and with a station free; with none, its ground control station exactly as before; with neither, the nearest such airfield anyway (it holds there until a station frees, re-checking every second), else it orbits on until it runs dry. It heads home at bingo fuel (the flight to the hold point, the approach and its `holdReserve` of 60 s) or when already homing (sent by ReturnToBase or Rearm; since E2 never because it is empty). Joining, it reserves a station (a loading station when it needs rearm or repair, else an open stand; then the shortest taxi from its landing exit, then the station ID) and joins the airfield's landing queue (request tick, then ID). *Holding:* it orbits 25 cells round a hold point 25 cells beyond the runway's east end, at 40 m plus 15 m per queue place (4 places, then shared; 16 round 20 out since 2026-10-24). *The runway lock:* one aircraft at a time, from its clearance to land until it turns off onto its exit connector, or from its line-up until 2 s after wheels-up. Each tick the airfield system (airfields in ID order) clears the head of the landing queue once it is at the hold point, otherwise the longest-waiting aircraft at the line-up. (Since 2026-10-24 the approach sequence: a landing aircraft takes the runway where it leaves its join, and the next is cleared from the hold before that; "Main air base and radar (H1)", "The faster airfield".) *Contra-flow:* landings come from the east end toward the facility, takeoffs leave from the westmost connector away from it. *Final:* the join flies to the tangent point of a turn circle (1.5 x its approach turn radius) tangent to the extended centre line at an entry point (the fix plus twice the larger of 10 cells and that radius), rounds it, and rolls out there inbound; then it steers at a carrot 10 cells ahead on the centre line, slowing to its approach speed and down to the fix height (30 cells out, 15 m for the Watcher). At the fix it must be within 2 cells of the line, one facing step of the heading and at the fix height, else it goes around (the lock is freed, it rejoins the queue at its head). From the fix it glides straight at a point half the flare short of the touchdown point and rounds off over the flare into a level touchdown 8 cells in from the threshold (height and slope continuous; it never climbs), slowing to its touchdown speed. *On the ground* it follows a path of map points with every corner rounded (a quadratic curve of 8 pieces within 2 cells of the corner): the roll-out down the centre line (slowing at its roll-out deceleration to taxi speed) to the first connector ahead of where it reaches taxi speed, the taxi graph's shortest way (Dijkstra, ties by node index, never along the runway's centre line) to its row's lane node, then its station's park-in path to the stop mark; taxi speed, turn speed through corners over 30 degrees, braking ahead of them at its brake; from a standstill it first turns on the spot. An inbound aircraft waits at its lane mouth while another leaves that lane (pushing back, or taxiing out and short of the mouth), at most 10 s. *Parked:* fuel does not burn on the ground (roll-out to takeoff roll); M2 refuels at its 1x (`refuelTicks` for a full tank; no rearm, no repair, no station rates yet). With a full tank (and, after a Rearm order, its rearm and repair: M3b, E2) and `sortie`, it leaves on its own unless Stop held it; a Move or Attack order sends it off at once, serviced or not (a Move order also sets where it goes back to). It pushes back out of a nose-in station (its facing held), taxis to the holding point beside the line-up connector, waits for the runway, lines up, rolls to its takeoff speed (at its takeoff acceleration) and climbs out straight ahead at its flight block's climb rate, speeding up to cruise; then it flies free again, back to where it orbited or on to its order. With no airfield that serves it, a Watcher still lands at and is recovered by its ground control station. State: `Unit.AirOps` (phase, airfield, station, speed, path) and the airfield's `RunwayLock`, `Landings` and `Takeoffs`, hashed only while present; `World.FlightPhaseOf`, `World.AirfieldEvents` (cleared, go-around, touchdown, runway vacated, parked, departed, lined up, wheels-up) for renderers. Tests: `AirfieldFlightTests` (a made-up class-M jet with a 70-cell landing run lands only on a 4-segment runway).

**Airfield (M3a: craters, rubble, 2026-10-19).** *Craters and wear.* A blast whose point lies on the runway strip of a finished segment (a projectile reaching its point or something on the runway, an air strike, a drone strike, an instant splash hit) applies its weapon's `runway` field: missiles, drone strikes and heavy rockets dig a **small crater** (radius 1.5 cells, 15 s for one worker), the ballistic missile a **large** one (3 cells, 30 s); light rockets add **wear** to the 2 x 2 cell patch they land in (the 30-tube rocket 4, plus a quarter to each of the 4 neighbours). A patch at 20 becomes a small crater and it and its 8 neighbours start over; wear never heals on its own. Grass, taxiways and rows never crater. An airfield keeps at most 16 open craters: past that a new one enlarges the nearest to a large crater. Each crater closes the runway for its diameter plus 2 cells either side; the rest are **open stretches** (`Airfield.OpenStretches()`, `LongestOpenStretch(min)`: the longest one at least that long and holding a connector, or none). Tuned against the engine's own scatter: one full 30-rocket salvo at the middle of a 60-cell runway makes 1 or 2 craters (at least 45 of 50 seeds in the tests) and leaves no 36 cells for the Watcher; 5 stray rockets never crater, though their wear shows. Shots now pass over pavement (rows, segments, stands) instead of bursting on it. *Two-stage destruction.* At 0 HP the facility and the loading shelters become **rubble** at 50% of max HP (one HP bar; `Unit.Rubble`): rubble stops working (no rows or segments without a standing facility; a rubble station takes no aircraft) and can be hit again; at 0 again it is **gone**, and `Build` puts a new site on its own slot (`AirfieldFacilityCell`, `AirfieldStationCell`), rebuilt from scratch. A shelter falling on its parked aircraft takes 40% of the aircraft's max HP; parked aircraft take splash like ground units. *Repair* (`CommandKind.Repair = 27`, `OrderKind.Repair`): builders (the worker; the bulldozer later) repair an own repairable piece, standing or rubble, to full (one builder: the whole bar in half the build time; helpers 100/50/0), and rubble at full HP stands again; on a runway segment each builder takes the open crater nearest to it, walks to its edge and fills it (left as a repaired patch for the client), then the worn patch nearest to it (2 wear a second). Everything is integer and hashed only when present (craters, wear, rubble, repair progress); the golden replay is unchanged. Next (M3b): service rates, the facility-down rule, targeting and aircraft reactions to closed stretches (divert, go-around, stop).

**Airfield (M3b: service, recall, parked aircraft, crater reactions, 2026-10-19).** *Service.* A parked aircraft takes its station's service each tick, all integer with remainders: fuel at the station's refuel percent of the aircraft's 1x (`refuelTicks` for a full tank; 1x at both kinds), one round every `rearmTicksPerRound` x 100 / rearm percent ticks (the lowest stock short first), HP at `repairHpPerSecond` x the repair percent. The loading shelter rearms and repairs at 2x, the open stand at 1x: the Watcher loads a missile in 7.5 s or 15 s (both in 15 s or 30 s, the stand matching its ground control station's 30 s turnaround), repairs 20 or 10 HP/s and fills its tank in 20 s anywhere. A loading station (`needsFacility`) gives nothing at all (no fuel either) while its airfield's facility is rubble or gone; the stands keep their 1x (`World.StationWorks`). *Serviced* means full fuel, ammunition and HP (HP only where the aircraft and the station repair); serviced, a sortie aircraft leaves unless held. (Since E2 only a stop made on a Rearm order rearms and repairs; an automatic refuel stop and a recall refuel only: "Air combat", the owner's rules.) An aircraft at a shelter whose facility falls waits there unserviced (no relocation to a free stand) until the facility stands again or it is ordered off. Choosing a station it prefers a working one of the kind it wants (a loading station when it needs rearm or repair), then a working one of the other kind, then one that does not work. *Fallen stations.* A parked aircraft whose station falls (rubble, or gone) takes the M3a collapse damage, gets nothing more and stays put until ordered off (by its slot's way out even when the station is gone); it never sorties by itself from there. Reservations on a station that no longer stands are dropped: holding, the aircraft leaves the landing queue and reserves another (queued again once it has one); on final it reserves another (else it rolls out and stops off the runway past its exit); taxiing in it reroutes from the nearest taxi node to another free station, else it stops at its lane mouth (or where it is, past it) and waits there parked, taxiing to a station as soon as one frees. *ReturnToBase* (`CommandKind.ReturnToBase = 28`): an aircraft in free flight turns for home now: to the airfield of `Target` (any own piece) when it can take it, else its best home as in M2 (an airfield, else its ground control station). Recalled to an airfield it stays parked once refuelled (held, as Stop does; not rearmed since E2) until its next order; the ground control station relaunches as always (with the stocks it came home with). Climbing out it turns for home once it flies free; holding, a target airfield that can take it moves it there; on final or on the ground the order is ignored. *Parked aircraft.* On the ground (roll-out to takeoff roll, parked included: `World.Grounded`) an aircraft has its aircraft block's `groundArmor`, the new `parked_aircraft` class (the Watcher and the Overseer): every ground warhead's splash hurts it (bullets 60%, rockets and missiles 100-133%, the mortar 80%; see "Combat rules"); in the air it keeps its drone armor (`World.ArmorOf`). It is never an attack target, taxiing included (no weapon may be aimed at it; projectiles do not collide with it); only splash and a falling shelter reach it. Air defence ignores it: it is no air target on the ground, and a missile already chasing it loses it on touchdown. It sees `groundVision` (6 cells) from `groundEye` (2.5 m) with line of sight like a ground viewer, and is spotted on the fog grid like a ground unit (not by air-defence radar). *Craters.* An aircraft on final keeps the stretch it was cleared for; a crater cutting it sends it around (it lands on what is left if that is still long enough). Rolling (the roll-out to its exit turn, or the takeoff roll to where it would leave the ground) toward an open crater, it brakes for the crater's near edge: it stops short when v^2 / 2 brake fits in the distance (the Watcher's brake is 1 cell/s^2), else it runs to the edge and loses the type's `craterHitDamage` (50%) of its max HP there (events `CraterStop`, `CraterHit`). Either way it gives the runway lock back at once and taxis off by the nearest connector no crater separates it from: a landing aircraft on to its station, a departing one back to the holding point to wait for the runway. Holding with no usable stretch, every second it **diverts** (event `Diverted`): to another of the owner's airfields with a usable stretch and a free station (the nearest), else to its ground control station if that stands; with neither it holds on, re-checking every second, until the runway is repaired or it runs dry; an aircraft heading home with neither picks its own airfield with the closed runway and holds there. Parked and taxiing aircraft keep their stations; takeoffs wait at the holding point while the runway is closed for them. State: `AirOps` gains the service remainders, the cleared stretch and `Recall`, hashed only while present; content: the `parked_aircraft` armor class, `aircraft.groundArmor`, the type's `craterHitDamage` (the golden replay was re-recorded for the content hash only). `World.ServiceOf` (rates now, the next round's progress, serviced) is for the aircraft card. Tests: `AirfieldServiceTests`. *Placement on hills:* a new row, segment or rebuilt piece is not refused for steep cells that lie in the pad blend of a piece of its own airfield (the blend easing the ground onto the airfield's one pad height, which the new piece flattens to as well); its relief limit still applies, and ground steep on its own still refuses it (`AirfieldTests.ASiblingsPadBlendDoesNotMakeTheNextSlotTooSteep`).

**Air combat (E2 to E11, 2026-10-19/20).** The owner's flight rules, the mission layer and edges, fixed-wing flight, detection, signatures and cueing, air-to-air, air-to-ground, crash and ejection, the two jets (the multirole and the air superiority fighter, built by the airfield facility) and their airfield operations (the overhead break, the takeoff rule, the home check) are one chapter: "Air combat", below.

**Not yet:** the skirmish AI does not use the Watcher or the Overseer (it plays Resistance).

### Jericho type ballistic missile truck (2026-10-30)

The owner's spec (2026-10-29, the game repo's `docs/plans/jericho-spec.md`; every number a starting value): the Sentinel's
ground-launched strike missile, a **tier 2 pick** in its pool, its way to kill the air defence (an S-400 site first) without
risking a jet. **One truck and nothing else** (no compound, pad or silo: it sets up where it stands), **slow** (a slow truck
and a slow, very high flight), **the heaviest single hit**. The open questions were decided by the coordinator (decisions 759
to 762). Look (for the client): a big desert-tan 8x8 erector truck carrying one long white missile lying flat; jacks down and
the erector raises it to the vertical when it sets up. Content: `archetypes/sentinel.json` (`jericho_truck`),
`components/sentinel.json` (`jericho_missile`), `warheads.json` (`heavy_ballistic`), its line in `factions/sentinel.json`.
Engine: the weapon `terminal` block's `boostTicks` (a vertical start) and the `depotReload` block's `noService` (generic, opt-in;
`Sim/Systems/TerminalSystem.cs`, `Sim/Systems/ServiceSystem.cs`). Tests `JerichoTests` (18 tests, 23 cases). Decisions 759 to
769.

- **The truck.** 4,500 supply, 120 s to build, 500 HP, `medium_vehicle` armour, heavy wheeled (radius 1.3 cells), **speed 10**
  (1 cell/s; the hypersonic truck's 14), set up **20 s**, pack up **15 s**, a vertical launcher, tags `vehicle`, `launcher`,
  `heavy`, a driver like the other trucks, glyph `^`. Built at the vehicle garage or the military garage (both build Sentinel
  `vehicle`s); **no pick hook** yet (the pick lobby does not exist) and **no cap** (no exclusive group).
- **Range and orders.** **300 to 2,500 cells** (`minRange` 3000, `range` 25000: the whole 5 km map, the longest straight shot
  in the game). No spotter: any ground point it is ordered to, seen or not; a unit needs to be seen (every launcher's rule).
  **One missile**.
- **The flight** (weapon `terminal`, the hypersonic missile's closed form, H4, with a vertical start and a high, steep
  profile; the sim and renderers agree to the raw unit):
  - **the boost** (`boostTicks` 120, new, generic): for its first 6 s (fewer on a short shot, so the boost covers at most half
    the way to the peak: 42 ticks at 300 cells) it leaves the erector **straight up** and pitches over: its way along grows as
    `vc t^3 / (3 T^2)` (its speed over the ground from 0 to the projectile speed as the square of the time) and its height
    as `B (t / T)^2`, B set so the velocity is continuous where the arc takes over (`B = k vc T / 2`, k the rise's first
    slope). Measured: 21 m up at 1 s, 188 m at 3 s on a 1,000-cell shot; 32 m and 288 m at 2,500 cells (300 m at tick 62);
  - **the arc** at **119 cells/s over the ground** (`projectileSpeed` 1190): its peak **half the ground range** over the higher
    of the truck and its point (`peak` 50), **at least 600 m, at most 2.5 km** (`peakMin` 6000, `peakMax` 25000: out of the
    camera's view), **60% of the way** to the dive (`peakAt` 60);
  - **the fall** down to the separation point **20 cells out and 200 m up** (`from` 200, `slope` 5000), about 64 to 67 degrees
    down there on a 1,000 to 2,500-cell shot (74 at 300), then the last stretch at the same speed over the ground (`speed`
    1190: no speed jump at the separation), **near vertical, about 81 degrees** at the end, no weave.
  Flight times, launch to impact: **2,500 cells 501 ticks (25.05 s)**, 2,000 cells 417 (20.9 s), 1,000 cells 249 (12.45 s),
  300 cells 79 (3.95 s). The `Climb` phase is the first 120 ticks (display only). On terrain a hill in its way stops it (never
  during the boost); in its last 2 cells it hits the first unit or structure under it.
- **Damage.** **2,500** on the new **`heavy_ballistic`** warhead (the `ballistic` table with 250% to buildings instead of 220%):
  **6,250 to any building**, so every building falls at once (the heavy hangar's 6,000 HP is the most) and every tunnel
  entrance; 4,250 to armour (an infantry carrier or a tank in the inner ring dies). **Full within 8 cells, a flat 30% out to
  16**, scattered up to 2 cells an axis, a large runway crater. The weapon's `fire` block lights every forest cell inside 8
  cells, spreading. The heaviest missile, shell or rocket in the game; only bombs hit harder (the MOAB type bomb's 3,000 with its
  15-cell core that destroys everything, the bunker buster's 6,000 within 3 cells; decision 766).
- **Interception.** Air class **`ballistic`**: the Patriot and THAAD types would engage it (only if the Resistance ever had
  it: it has neither); the **S-400 type does not** (it stays aircraft only, the owner's S-400 spec), nor the short-range air
  defence. Nothing engages it in its last 20 cells (the terminal dive, `World.Evades`).
- **Counterplay.** Every enemy is warned **10 s** before the impact (`warn` 200; `World.IncomingStrikes`, `IncomingEvents`);
  **each launch shows the truck to every enemy for 30 s** (`reveal` 600). Hunt the slow truck, its 20 s set-up and the reveal.
  The launch's lingering smoke cloud (about 20 s in the spec) is the client's: no smoke screen in the sim.
- **Reload.** Only at a **supply depot or a field ammo point** (any structure with a depot's supply: not a store, not a garage),
  **packed up**, **180 s and 1,000 supply** (`depotReload`), 1 s after the shot; an idle set-up truck there packs up first. A
  garage's service (the military garage, a forward airbase's field garage) repairs it but **never reloads** it
  (`depotReload.noService`, new: `ServiceSystem` skips the stock: no need, no refill, no charge).
- **For renderers.** As the hypersonic missile: `World.FlightPointAt(p, tick, permille)` (the boost in it: straight up, then
  pitching over), `ImpactTickOf`, `TerminalPhaseOf`, `IncomingStrikes`, `IncomingEvents`, `RevealedBy`, `Detonations`,
  `FireEvents`. The truck's `Deploy` state drives the jacks and the erector.
- **Hashing.** Nothing new per unit or projectile (the boost is derived from the hashed origin, heights and peak). The content
  hash adds `boost` and `noService` only when set: content without them hashes as before.

### Assault medic (2026-10-21)

The Sentinel's first soldier of its own (content `sentinel_medic` in `archetypes/sentinel.json`, `medic_kit` in `components/sentinel.json`; `Sim/Systems/AuraSystem.cs`; tests `MedicTests`). The owner: "its basically an assault medic, units around it heal, units cannot stack healing, only take 1 healing if multiple healers"; the numbers were left to us.

**The soldier.** A rifleman of a regular army carrying an aid bag; Sentinel only, `infantry` and `soldier` (he counts against defeat and may drive), a fixed loadout (no preset), no mines.

| | Assault medic | Resistance rifleman (`assault`) | Why |
| --- | --- | --- | --- |
| HP | 75 | 60 | a plate carrier: 7 rifle hits to kill, not 5; a little tougher, not a tank |
| Speed | 18 | 19 | the bag (the engineer's 18) |
| Vision | 7 cells | 7 cells | the same eyes |
| Weapon | rifle (12, 5 cells, 0.75 s) | rifle, LMG or SMG + pistol | an assault soldier who fights too |
| Cost | 260 (200 + rifle 10 + kit 50) | 165 (rifle, pistol, both mine kits) | about 1.6 riflemen: the heal pays only for a group fighting near him |

**The aura** (the `aura` effect kind, now run by a system; any component may carry one). Every **1.5 s** (30 ticks) the medic heals **3 HP** to each of his side's units with the `infantry` tag within **6 cells** (the rifle's 5 plus a cell, so the squad he fights with is covered), never above max HP: **2 HP a second**, a half-dead rifleman (30 of 60) whole again in 15 s. Against one enemy rifle's 16 HP a second that is an eighth: it decides long fights and recovers a squad between fights, it does not save a soldier under concentrated fire.
- *Who is healed:* people on foot (`infantry`: soldiers, workers, a downed pilot); never vehicles or structures (no such tag), enemies or neutrals. Healed under fire too (no "not hit lately" rule): he is an assault medic, and the rate is low enough.
- *No stacking:* a unit takes **at most one aura heal per interval** however many medics stand near: the strongest aura that reaches it (most HP per tick, then the larger single heal, then the lowest source ID), and then none until that aura's interval has passed (`Unit.HealReady`). Two or three medics in a group heal it no faster than one; they spread the cover instead.
- *Not himself:* a medic never heals himself; another medic heals him.
- *Inside:* nobody inside a vehicle (driver or rider), a building, a tunnel or a garage bay is healed, and a medic inside one heals nobody. A garrison is out of reach for two reasons: occupants are off the map (not seen, not targeted), and the only way to hurt them is explosives on the building (`occupantDamage`); a building that also regenerated them would be nearly unbreakable.
- *Determinism:* units in ID order, sources in ID order with the tie-breaks above, integer only; `HealReady` is hashed only once set, so games without auras hash as before. Aura `modifiers` still load but nothing applies them yet.

**How he is obtained:** two start in the Sentinel lobby's army (`content/setups/sentinel.json`); the sandbox spawns him by archetype. There is no infantry production yet (the garages build vehicles).

### Humvee (2026-10-24)

The Sentinel's light raider and troop carrier (content `humvee` in `archetypes/sentinel.json`, `humvee_mg` and
`smoke_launchers` in `components/sentinel.json`, the `heavy_bullet` warhead; `Sim/Systems/SmokeSystem.cs`, the staged unload
in `CrewSystem.cs`; tests `HumveeTests`). The owner (the game repo's `humvee-spec.md`): an up-armoured HMMWV type with a
gunner turret and a .50 cal, powerful against infantry, a driver plus 5 riders, fast on the ground; it **replaces the
technical in the Sentinel roster** (the technical stays Resistance only), the gun is ground only, smoke launchers and a fast
dismount, no anti-air, no variants. The numbers are ours (decisions 237 to 250).

| | Humvee | Technical (Resistance) | Infantry carrier |
| --- | --- | --- | --- |
| Armour, HP | `medium_vehicle`, 400 | `light_vehicle`, 180 | `armored_vehicle`, 1,200 |
| Speed | 40 (the fastest Sentinel ground vehicle) | 35 | 28 |
| Cost, build | 500, 15 s (either garage) | 300, 10 s | 700, 20 s |
| Gun | `humvee_mg`: 14 at 7 cells, 0.25 s, ground only, on the move | `heavy_mg`: 12 at 6 cells, also low air | none |
| Riders | 5 `infantry`, out 0.4 s apart | none | 15, all out at once |
| Extras | 2 smoke pops | | |

**The gun.** A stronger, ground-only cousin of the technical's heavy MG in a turret that fires on the move. Its anti-infantry
edge is a warhead, the simplest fit for the weapon model (a burst splash would hit its own riflemen beside the target):
`heavy_bullet` is the bullet's table with **125% against infantry**, so a burst takes 17 from a soldier (the heavy MG 12): a
60-HP rifleman falls to 4 bursts (0.75 s) instead of 5. Vehicles take 40% (5 a burst: a technical in 36 bursts, 9 s), buildings
10%. Against it, the Kornet's missile does 69 (55% on medium armour: 6 hits), an RPG 84 (70 x 120%), a light rocket 36.

**Passengers and the fast dismount.** The crew block's new `unloadInterval` (ticks): Exit on the vehicle lets its riders out
one by one, the first at once and the next every interval, each on the next free cell behind it; the Humvee's 8 ticks put
all 5 out in **1.6 s**. 0 (every other vehicle, the infantry carrier included) keeps the old all-at-once unload. The unload
goes on while it drives (riders drop behind it along the way), pauses while it is inside something, and a second Exit does not
restart it; destroyed mid-unload, the rest bail out by the crew survival rule. Boarding is unchanged.

**Smoke launchers** (the `smoke` effect: `charges`, `radius`, `duration`, `cooldown`; any component may carry one: the tanks
next). `CommandKind.Smoke` (41) pops a screen around each listed unit where it stands (a charge; it needs a driver and its
launchers reloaded: `cooldown`). The Humvee: **2 pops**, **12 cells across** (radius 6), **10 s**, 2 s apart. A screen stays
where it was popped (driving out of it, the vehicle is visible again unless the screen is between).
- *Sight:* a line from a viewer to a ground unit is blocked by a screen it crosses, unless both ends stand in that screen
  (an air viewer is never in one): enemies outside can't see units in it or behind it, units in it see nothing outside, and
  two units in the same screen see each other. The vision system re-checks only units near a screen that an enemy side spotted:
  that side keeps its bit if one of its viewers has a clear line within its sight range (terrain is not checked again: the fog
  grid already showed the cell). Ground radars do not see into a screen but do see past one; flying units are never hidden.
  The fog grid itself is unchanged (the client draws the screen); without fog the map shows everything but what stands in a
  screen, so an ordered attack holds fire at a unit in smoke (one ordered unit walks toward it, and fires once it stands in
  the screen itself).
- *Guided missiles:* one whose target stands in a screen, or (operator-guided: the Kornet) whose operator's line to its
  target crosses one, loses guidance: it flies on for its drop distance and goes off on the ground (`LockBroken` event).
  Fire-and-forget missiles (the helicopters') lose a target in a screen. The Watcher's strike, bombs and air defence are not
  guided this way and are unaffected.
- *Charges* refill where weapons rearm: a supply depot and the field ammo point (a charge a pulse, the ammo point's round
  cost), and a garage's service (the military garage, the forward airbase's field garage, paid as a round from its stock).
- *For the client:* `World.Smokes` (`SmokeCloud`: id, source, owner, centre, radius, since, until), `World.SmokeEvents`
  (`Popped`, `Expired`, `LockBroken` with its missile), `World.SmokeOf(unit)` (charges, max, ticks to ready),
  `World.SmokeAt(point)`, `World.InSmoke(unit)`; the seats from `PassengersOf` and `Def.Crew.Passengers`; a staged unload in
  `Unit.DismountWait` (ticks to the next) and `Unit.Dismounted`.
- *Determinism:* screens in pop order (IDs), integer geometry (128-bit products for the line test), hashed only once one was
  popped; a unit's `DismountWait`, `Dismounted` and `SmokeReady` only for units with smoke or a staged unload.
- *Measured* (Release, M3 Pro): 40 screens at once among 880 units all in sight of one another, with fog: tick 0.29 ms ->
  0.57 ms; without screens nothing runs.

**Not yet:** the skirmish AI does not pop smoke or use the dismount; no infantry fires out of it (no fire ports, the engine
rule); the screen does not drift (no wind).

### Main battle tank (2026-10-24)

The Sentinel's first tank, an M1A2 SEP v3 type ("Abrams"; content `main_battle_tank` in `archetypes/sentinel.json`,
`tank_120mm`, `tank_mgs` and `trophy_aps` in `components/sentinel.json`, the `apfsds` and `he_mpat` warheads;
`Sim/Systems/ArmorSystem.cs`, `Sim/Systems/ApsSystem.cs`, the coaxial fire in `CombatSystem.cs`; tests `AbramsTests`). The
owner (the game repo's `abrams-spec.md`): 40 main gun rounds; directional armour (strong front, weaker sides, weak rear, the
roof weakest against top attack); Trophy active protection with 3 charges that can fail by the threat's speed and range, a
seeded roll. The numbers are ours (decisions 251 to 271). Every new mechanic is generic and opt-in (the M10 Booker light tank
next reuses the rounds, the armour and the ammo; it gets smoke, no Trophy).

| | Abrams | Infantry carrier | Humvee |
| --- | --- | --- | --- |
| Armour, HP | `armored_vehicle`, 2,500, directional | `armored_vehicle`, 1,200 | `medium_vehicle`, 400 |
| Speed, radius | 30 (tracked), 12 | 28, 11 | 40, 8 |
| Cost, build | 2,500, 45 s (military garage only) | 700, 20 s | 500, 15 s |
| Gun | 120 mm, 40 rounds, HE 250 or dart 900, 20 cells, 6 s | none | `humvee_mg` |
| MGs | `tank_mgs` 12 at 7 cells, 0.25 s, coaxial, unlimited | | |
| Protection | Trophy (3), smoke (2) | | smoke (2) |
| Crushes | soldiers and the light cars (crusher 3) | soldiers (2) | soldiers (2) |

**Where it is built.** The military garage only (its bays are 16 m apart; the vehicle garage's doors 8 m): the production
block's new `excludeTags` (the vehicle garage: `["tank"]`; `ProductionSpec.Builds`). In a military garage bay the tank (radius
12, the heavy trucks') goes in and out like any vehicle (`TheMilitaryGarageRearmsItAll`: in and out in 229 ticks). Not in the
Sentinel lobby: it is built. A driver (a soldier), no passengers (the 4 crew show in the art).

**The gun: rounds by target.** Two weapon effects in one component, the second marked `sharedAmmo` (it draws on the stock of
the weapon before it in the component; its own `ammo` is 0): high explosive first (`he_mpat`, 250, a 3-cell splash from 100%
to 20%, 60 cells/s; buildings 120%, armour 25%), the armour-piercing dart second (`apfsds`, 900, a 0.3-cell splash, 150
cells/s; every vehicle 100%, soldiers 10%, buildings 15%). The usual pick (the most damage per tick against the target's
armour) is the round choice: the dart against every vehicle, HE against soldiers and buildings; an attack-ground order fires
HE (the first ground weapon). 40 rounds shared, 20 cells (the Kornet's 28.8 still outranges it), a round every 6 s, on the
move (its turret). Its vision is 8 cells: beyond that it fires at what a friend spots.

**The machine guns.** The coaxial 7.62 and the commander's .50 cal are one weapon, `tank_mgs` (12 a burst through
`heavy_bullet`: 15 to a soldier, at 7 cells every 0.25 s, ground only, unlimited). The new weapon flag `coaxial` gives it its
own reload clock (`Unit.CoaxReload`): every tick its turret is on the tank's target it fires at it alongside the gun if it
can hit and reach it (`FireCoaxial`; `Unit.CoaxShot` is the tick of its last burst, for the tracers). The weapon pick takes a
coaxial weapon only when no other weapon can fire at the target, so with the 40 rounds gone (or a target out of the gun's
reach) the MGs carry on alone. They fire at the tank's own target only (decision 256).

**Directional armour** (archetype `directionalArmor: { front, side, rear, top, frontArc, rearArc }`, opt-in; nothing else
changes). Every hit on the unit (`Hurt`: a direct hit, a splash share, a max-HP share) is scaled by where it lands:

| Where | Percent | When |
| --- | --- | --- |
| Front | 40 | it comes from within 45 degrees of the hull's nose (`frontArc` 90: 4 facings either side) |
| Side | 80 | between the arcs, or from an unknown direction (a mine under it) |
| Rear | 130 | within 45 degrees of the tail (`rearArc` 90) |
| Top | 150 | from above: a lobbed round (an arc or a salvo: mortar, rocket artillery, the twin and ballistic missiles, one-way drones), a flying shooter (aircraft, helicopters, the Watcher's and the Kaman's missiles, bombs, strafing), an FPV drone's strike, a falling wreck |

The direction: a splash's from where it went off (when that is off the unit's centre); a projectile's direct hit from where it
was launched (a straight shot's line; a guided missile from the way it flies in); an instant shot's from the shooter. The hull's
facing decides (the turret is not modelled apart). Integer percents of the damage, `HitFacing` at the 32-facing resolution;
nothing hashed (the context is scratch). `World.ArmorHits` reports each hit (unit, facing, percent, HP taken, by) for the
client's "where hits came from" diagram.

*Measured* (`AbramsTests`; HP 2,500, hits to kill):

| Weapon | Front | Side | Rear | Top |
| --- | --- | --- | --- | --- |
| The tank's dart (900) | 360 (7) | 720 (4) | 1,170 (3) | |
| Kornet (126, atgm) | 50 (50) | 100 (25) | 163 (16) | |
| RPG (70, rocket 120%) | 33 (76) | 67 (38) | 109 (23) | |
| FPV drone (70, explosive) | | | | 94 (27) |
| Attack helicopter's missile (500) | | | | 750 (4) |

Existing outright kills stay (decision 261): an AT mine destroys it like any vehicle, and the Watcher's strike destroys it
80% of the time (it is `heavy`) unless Trophy stops the missile.

**Trophy active protection** (the `aps` effect on any ground unit: `{ charges, range, cooldown, chance, fastFrom, fastStep,
fastPenalty, closeRange, closePenalty, minChance }`; `Sim/Systems/ApsSystem.cs`). The tank's: **3 charges**, refilled where
weapons rearm (a supply depot and the field ammo point a charge every 2 s at a round's cost, a garage's service).
- *Threats:* projectiles of a weapon marked `apsThreat`: the RPG (`at_missile`), the Kornet, the Kaman drone's missiles, the
  helicopters' two missiles and the Watcher's strike missile. Never bullets, tank shells, bombs, artillery, rocket salvos or
  drones (decision 263: drones are the owner's open question).
- *When:* before each projectile moves, a threat aimed at a unit with an APS (its target: a guided missile still guided, a
  strike missile whose launch roll hit) gets **one look** once it is within the APS's `range` (4 cells) plus its own step
  that tick. No charge: `Empty`. Still recovering from its last engagement (`cooldown`, 1 s): `Overwhelmed` (a salvo of
  two gets one through). Otherwise it **engages**, spending a charge whatever happens, and rolls.
- *The chance:* `chance` (90%), less `fastPenalty` (10%) for each `fastStep` (10 cells/s) of the threat's speed above
  `fastFrom` (25 cells/s), rounded up, less up to `closePenalty` (40%) in proportion as the launch point was closer to the
  unit than `closeRange` (20 cells), never below `minChance` (10%): a Kornet from afar 90%, from 15 cells 80%; an RPG from
  its full 9 cells 68%, from 5 cells 60%; a 40 cells/s missile (helicopter, Kaman) 70%. The roll is `Rand.Hash` of the
  seed, the tick, the unit and the threat (no generator state). `World.ApsChance(aps, speed, distance)` and
  `World.ApsChanceAgainst(unit, projectile)` give it to the client.
- *Outcome:* `Intercepted`: the threat is gone 4 cells out (where it is, if it came in closer), a guided missile's operator
  reloads; the blast hurts nobody. `Failed`: it flies on and hits as usual.
- *Measured* (`TrophyRatesMatchTheChance`, 200 seeds): a Kornet from 25 cells stopped 183 times (90%), an RPG from 8 cells
  126 (66%); helicopter missiles 14 of 20 (70%).
- *For the client:* `World.ApsEvents` (`ApsEvent`: kind, unit, projectile, shooter, point, chance, charges left; kinds
  `Intercepted`, `Failed`, `Empty`, `Overwhelmed`), `World.ApsOf(unit)` (charges, max, ticks to ready).

**Crushing.** Crusher 3: it runs over soldiers and the light cars, now `crushable` 2: the dirt bike, the technical and the
rocket jeep (nothing else crushes a vehicle: every other crusher is 2). Trucks, launchers, armour and the Humvee are never
crushed.

**Rearm.** The rounds are a plain ammo stock: a supply depot (1 a round) and the field ammo point (20 a round) give one round,
one Trophy charge and one smoke pop every 2 s each (40 rounds in 80 s; a full rearm at an ammo point costs 900); the military
garage and the forward airbase's field garage refill everything in their 5 s.

*Determinism and hashing:* projectiles in ID order, integer only; a unit's `ApsReady`, `CoaxReload`, `CoaxShot` are hashed only
for units with an APS or a coaxial gun, a projectile's launch point (`Projectile.Launched`) and its APS look (`ApsSeen`) only in
a match with an APS or directional armour: games without the tank hash as before.

**Not yet:** the skirmish AI does not use it (it plays the Resistance); the MGs do not pick a target of their own; the turret
and hull are one armour facing; no drone interception (owner question).

### Light tank (2026-10-24)

The Sentinel's light tank, an M10 Booker type ("Booker"; content `light_tank` in `archetypes/sentinel.json`, `light_tank_105mm` in
`components/sentinel.json`, the airlifter's `airlift` block in `archetypes/logistics.json`; `Sim/Systems/AirliftSystem.cs`; tests
`BookerTests`, `AirliftTests` (9 tests, 15 cases)). The owner (the game repo's `booker-spec.md`): faster than the Abrams for sure; not bad against
tanks, just weaker; it can fight the missile launchers for sure; built at the military garage, and a C-5 can fly 2 of them to a
forward airfield (no airdrop); 35 rounds; smoke and lighter directional armour; no Trophy. It reuses the Abrams' rounds by target,
directional armour, coaxial MGs and ammo, and the Humvee's smoke. The numbers are ours (decisions 272 to 293).

| | Booker | Abrams |
| --- | --- | --- |
| Armour, HP | `armored_vehicle`, 1,400, directional 60 / 100 / 140 / 160% | `armored_vehicle`, 2,500, 40 / 80 / 130 / 150% |
| Speed, turn, radius | 38 (tracked), 3, 11 | 30, 2, 12 |
| Vision | 9 cells | 8 cells |
| Cost, build | 1,500, 30 s (military garage only), or airlifted | 2,500, 45 s |
| Gun | 105 mm, 35 rounds, HE 170 or dart 600, 18 cells, 6 s | 120 mm, 40 rounds, HE 250 or dart 900, 20 cells, 6 s |
| MGs | `tank_mgs` (the same) | `tank_mgs` |
| Protection | smoke (2) | Trophy (3), smoke (2) |
| Crushes | soldiers and the light cars (crusher 3) | the same |

**The gun.** `light_tank_105mm` is `tank_120mm`'s scheme: HE first (`he_mpat`, 170, a 2.5-cell splash 100% to 20%, 60 cells/s),
then the dart (`apfsds`, 600, `sharedAmmo`, 150 cells/s), 35 rounds shared, 18 cells, a round every 6 s, on the move; the usual
pick takes the dart against vehicles and HE against soldiers and buildings. The MGs are the Abrams' `tank_mgs` (coaxial,
unlimited). Rearm as the Abrams: a round and a smoke pop every 2 s at a depot (1 each) or the field ammo point (20 each), all at
once in a garage's service (measured: 35 rounds and 2 pops from an ammo point for 740).

**Against tanks** (measured, `BookerTests`; hits to kill):

| Shooter on target | Front | Side | Rear |
| --- | --- | --- | --- |
| Booker on Abrams (600) | 240 (11) | 480 (6) | 780 (4) |
| Abrams on Booker (900) | 540 (3) | 900 (2) | 1,260 (2) |
| Booker on Booker | 360 (4) | 600 (3) | 840 (2) |
| Kornet on Booker (126) | 75 (19) | 126 (12) | 176 (8) |
| RPG on Booker (70, rocket 120%) | 50 (28) | 84 (17) | 117 (12) |

The spec's "about 30 head-on, 10 to 13 from the side or rear, the Abrams in 8 frontal hits" assumed the Abrams' dart at 300; it
landed at 900 (decision 254), so every count is a third and the dart is 600, two thirds of the Abrams' (decision 275): head-on
it needs 11 hits while the Abrams needs 3 (hopeless: a head-on duel at 15 cells, measured, the Booker dead at 12.1 s with the
Abrams at 1,780 of 2,500); from the side 6 and from behind 4, so two Bookers (3,000 against 2,500) on its tail and flank kill an
Abrams in two volleys (measured 6.1 s, one Booker hit once). It wins by flanking with its speed: 40 cells in 10.6 s, the Abrams
13.3 s (measured, open ground).

**Against the launchers.** Every Resistance launcher is a `medium_vehicle` of 150 to 300 HP, and the dart does 600 to each: **one
shot** (measured, side on at 14 cells): the light, medium and heavy rocket trucks and the rocket truck, the rocket jeep, the twin
missile truck, the Kaman and drone launcher trucks, the ballistic missile truck, the SA-6 launcher and the mobile radar. Their
weapons against it (content as landed): the light and medium rocket trucks' direct rockets reach only 6 and 12 cells (its gun
18); the rest have a minimum range: the rocket truck's and the drone rails' 15 cells, the jeep's 20, the twin missiles' 45, the
heavy rocket truck's 50, the Kaman's and the ballistic missile's 60 (the SA-6 and the radar have no ground weapon). A Booker that
closes to 15 cells is inside every one of them; the Kornet (28.8 cells) and the RPG are its threats, and smoke breaks a Kornet's
lock (`SmokeBreaksAKornetsLock`).

**Protection.** Directional armour 60 / 100 / 140 / 160% (front / side / rear / top, the Abrams' 90-degree arcs); the Humvee's
smoke launchers (2 pops); no Trophy (`ApsOf` is null). It carries the `heavy` tag: the Watcher's strike destroys it 80% of the
time (decision 274).

**The airlift** (the airlifter's `airlift` block, `AirliftSpec`: `{ seats, tags, ramp, boardRange, boardTicks, unloadTicks }`;
generic: any runway aircraft may carry one). The airlifter: 2 seats for `airliftable` vehicles (only the Booker), the ramp point
11.5 cells behind its centre (its tail is 10), 4 cells to start up, 10 s each, 5 s between drive-offs.
- *Boarding:* `Enter` (4) with the airlifter as the target, while it is parked at a station of an airfield of its side with
  unlimited supply (a main air base: `World.AirliftDocked`); refused otherwise, and for anything without one of the tags
  (`World.CanAirlift`). Each vehicle drives to the ramp point or, when its class cannot stand on the ramp point's cell (in a heavy
  bay it is just inside the door, ground the routing keeps a tank's hull off), to the nearest cell it can stand on
  (`World.AirliftBoardSpot`: straight-line, ties by row then column; in the heavy bay 3.04 cells from the ramp point; decision
  314) and, within 4 cells of that spot with the ramp free, goes up it in 10 s (`Unit.AirliftBoard` counts down; one at a time, the lowest ID first), then is aboard
  (`Unit.Inside` the airlifter: off the map, its driver in it). The first up takes the 4 containers off (free: back into the
  base's unlimited supply; `PalletsOff`), and boarding holds the airlifter parked until its next order. Measured: two Bookers 75 cells
  off in a straight line (they drive round the base), the first up the ramp 24.9 s after the order, both aboard 20 s after that; from 6 other sides, the
  north and north-west among them (where the second used to stall 4.1 to 4.4 cells from the ramp point), 26.6 to 39.4 s, the second
  always up the ramp within a tick of the first being aboard (`TwoBookersBoardFromAnyApproach`).
- *Tanks or containers, never both:* while a seat is in use (`Unit.Lift`: aboard or on the ramp) its service loads no containers
  and an Airdrop order is refused (`NoAmmo`); emptied, it loads them again at home.
- *Delivery:* `Deliver` (39) now also takes an airlifter with vehicles aboard: it flies to the forward airbase, lands, taxis to a
  stand and, there, the first vehicle drives off 5 s after it parks (the ramp coming down) and the next 5 s later, each put on
  the ramp point facing away and sent to the field garage's rally point (`RallyCell`; no garage: they stay at the ramp); then it
  flies home and loads containers (the delivery rule). Measured: ordered at the main base 154 cells away, parked at the forward
  airbase 136.6 s later, off at +5 s and +10 s, both stopped at the rally point 16.3 s after the second; then home and loaded again.
- *Held with them aboard* (decision 315, replacing 292): with a vehicle aboard or on its ramp it never leaves on its own. A Rearm
  order (or the implicit service of any visit) gives it its service, fuel and HP (no containers with vehicles aboard), and then it
  stays parked, held, until an order: Deliver, Move, an air task. Measured: half fuel and half HP, Rearm, 120 s later still parked
  at the main base, full, held, both aboard; a Move takes it off with them (`ARearmOrderKeepsItParkedWithItsTanks`).
- *Exit* on the parked airlifter (any airfield) lets every vehicle out at once onto the free cells behind it; Exit on a vehicle
  aboard, or its driver, is ignored.
- *Losses:* destroyed with vehicles aboard (shot down, or on the ground), they and their crews are lost (`Lost` events; no
  survival roll), as its containers are.
- *For the client:* `World.AirliftEvents` (`AirliftEvent`: kind, aircraft, vehicle, where; kinds `Boarding`, `Aboard`,
  `PalletsOff`, `DroveOff`, `Lost`), `World.AirliftAboard(u)`, `World.AirliftOf(u)` (`AirliftStatus`: aboard, seats, the
  vehicle on the ramp, its ticks left), `World.AirliftRamp(u, spec)`, `World.AirliftDocked(u)`, `World.CanAirlift(vehicle, u)`,
  `World.AirliftBoardSpot(vehicle, u)`, `Unit.AirliftBoard`, `Unit.Lift`, `Def.Archetype.Airlift`. The visor, the ramp, the drive up and off are the client's.
- *Determinism:* units in ID order; the seats in use are recounted every tick before boarding; `AirliftBoard` and `Lift` hashed
  only when not 0: games without the airlift hash as before.

**Not yet:** no skirmish AI use; no airdrop of vehicles (owner: none); vehicles aboard keep their last position.

## Air combat

The air-combat framework every aircraft flies under (the Watcher, the Overseer, the scout drone and the two jets), built in
the night of 2026-10-19/20 as milestones E2 to E11 of the game repo's jets plan (its section 2, "the air-combat model").
This chapter is the as-built reference: the rules, the numbers, the content fields, the balance levers and the hooks for
renderers. The airfield itself (pieces, landing, taxi, service, craters) is "Airfield (M1)" to "(M3b)" under the Sentinel
faction; the unit write-ups (the Watcher, the Overseer) are there too. Decisions the night took on its own are listed
at the end of this chapter ("Decisions to confirm") with the commit that took each.

**Code and tests.** `Sim/AirMission.cs` and `Sim/Systems/MissionSystem.cs` (missions, the flight controller, edges),
`Sim/FixedWing.cs` and `Sim/Systems/FixedWingSystem.cs` (the jets' kinematics), `Sim/Systems/AirDefenseSystem.cs`
(detection, cueing, AA and ground guns), `Sim/Systems/AirCombatSystem.cs` (air-to-air), `Sim/Systems/AirStrikeSystem.cs`
and `Sim/StrikeRun.cs` (air-to-ground), `Sim/Systems/LossSystem.cs` and `Sim/Losses.cs` (wrecks, ejection),
`Sim/Systems/AirfieldFlightSystem.cs` (the pattern, the takeoff rule, the home check). Suites: `AirRulesTests`,
`AirMissionTests`, `FixedWingTests`, `AirClassTests`, `JetTests`, `AirToAirTests`, `AirToGroundTests`, `LossTests`,
`JetAirfieldTests`, plus the airfield and drone suites they leave green. All integer; every roll a `Rand.Hash` of the
seed, the tick and the IDs involved (no generator state); units visited by ID; new state hashed only while present
(`Unit.AirMission`, `Unit.Wing`, `Unit.Strike`, `Unit.Salvos`, `World.Wrecks`, `World.Chutes`), so content without
aircraft hashes and plays as before (the golden match has none).

### The three layers

Every aircraft flies three layers. The **mission** (what it does: the task below) picks a point to steer at; the **flight
controller** (pure functions) turns that into a turn demand, edge avoidance first; the **kinematics** of its kind move
it: a drone (no `flight.kind`, or `"drone"`) turns its velocity by at most its turn rate a tick as it always did, a fixed
wing (`"fixedWing"`, the jets) flies by energy within its envelope. Guard, strikes, bingo, the airfield and edge
avoidance are the same code for both.

### Modes and the owner's rules

| Task | Given by | Picks its own targets | Fires | Notes |
| --- | --- | --- | --- | --- |
| **Orbit** | a Move order (no mission) | no | only on an attack order | circles `OrbitAt`; Move ends any mission, Stop ends it and orbits where it is |
| **Patrol** | AirTask `Patrol` | no | no (it spots) | one waypoint: an orbit; two: a racetrack; 3 to 8: a closed loop; `queue` (Shift) appends; a waypoint is reached within the turn radius plus 2 cells |
| **Guard** | AirTask `Guard` over an area (`cell` to `cell2`: a racetrack along its long axis) or a route, with a `filter` | **yes**: the kinds in its filter inside the area plus `mission.huntLeash` (or within the leash of its route) | yes | the only automatic mode; filter 0 takes the archetype's `mission.guard` (Watcher: vehicles and structures; multirole: vehicles, structures, drones; fighter: aircraft, drones) |
| **Search** | AirTask `Search` | no | no | lawnmower lanes along the long axis, 1.6 x its sight apart; done, it orbits the centre, or with `on` starts over; progress in half lanes |
| **Roam and spot** | AirTask `Roam` | no | no | seeded waypoints (`Rand.Hash(seed, unit, leg)`), each in an 8 x 8 sector not flown over since its memory filled |
| **Strike** | an attack order (a unit) or attack-ground (a point) | only the ordered target | yes | runs on top of the task; the task resumes after |
| **Rearm** | `CommandKind.Rearm` (29) | no | no | home (a loading station preferred), refuel, rearm, repair, then **resumes its task** (D10) |
| **ReturnToBase** | `CommandKind.ReturnToBase` (28) | no | no | home, refuel only, held parked until its next order |
| **Bingo trip** | automatic | no | no | the only automatic trip home: refuel only (D11), an open stand, then resumes |

Search and Roam without `cell2` take a square two sights across round `cell`. In every task (and on the way home) an
aircraft protects itself: flares, chaff and breaks against missiles homing on it.

**The owner's rules (E2, 2026-10-19).** (1) *Home on its own only to refuel:* bingo fuel is the only automatic trip
home (to the nearest airfield that serves it and whose pattern fits, else its ground control station); never because
it is out of ammunition (`homeWhenEmpty` is retired: accepted in content, ignored; 2026-10-21 exceptions: a gunship out of
rounds, decision 141, and a planned bomb pass that empties every stock, decision 143, go home to rearm). An empty aircraft orbits on, its
card showing no ammunition, until it is ordered to rearm or reaches bingo. An automatic refuel stop refuels only (no
rearm, no repair), takes an open stand (the loading shelters stay free) and leaves once the tank is full, back to its
task. At a ground control station a drone recovered without a Rearm order keeps its stocks and HP for its relaunch
(`Unit.KeptStocks`, also given to a manual launch of the same kind). (2) *Rearm only on command:* Rearm sends it home as
ReturnToBase does (that airfield when it can take it, else its best home); already landing or parked it takes the full
service where it is (ending a recall's hold); leaving (pushback to climb-out) it comes back once it flies free
(`AirOps.Recall`, `AirOps.RecallReason`). Why it is homing is `Unit.HomeReason` (`Refuel`, `Rearm`, `Recall`; hashed
while homing); `World.ServiceOf` reports rearm and repair rates of 0 on a refuel stop or recall. (3) *No target picking
of its own except on Guard:* combat and unit AI skip flying units; a strike happens only on an order or a Guard pick.

**Bingo** is the fuel to fly home at cruise burn (so an aircraft on afterburner reaches it sooner) plus the approach,
the hold reserve (`aircraft.holdReserve`: 60 s drones, 30 s jets) and `flight.reserve`. On a strike's run-in an
aircraft at bingo still finishes a release at most 3 s away (`StrikeReleaseDue`), then goes home. After the refuel it
resumes its task. A strike, a refuel trip, a Rearm visit or a recall keeps the mission (at an airfield on the unit; at a
ground control station `Unit.KeptMission` carries it to the relaunch); back in free flight it rejoins at the nearest
waypoint (a search at the start of its lane, a roam with a fresh waypoint). An AirTask given while homing or at the
airfield waits for its return (parked: it leaves at once, as a Move does).

### Flight

**Drones** fly the drone law: the velocity turned at most `flight.turnRate` (permille of the speed) a tick. The
controller's **turning home** rule: an aim behind the beam and beyond twice the turn radius turns it at its full rate
toward the aim's side (the drone law alone barely turned with home dead astern); closer in the old lazy law (its wide
swing brings it back over its station for recovery).

**Fixed wings** (E4; content `flight.kind: "fixedWing"` and a `fixedWing` envelope, compiled to `FixedWingSpec`, speeds
in raw a second so accelerations are exact integers). Cruise is the archetype's speed; `attack` is what full dry power
(military) holds level and is flown on a strike; `dash` is the afterburner's level speed and the most it ever flies;
`minSpeed` is the stall; `cornerSpeed` and `cornerRadius` give its g limit aG = corner^2 / radius. *Turn:* the lateral
acceleration available is a(S) = min(aG, aG x (S / corner)^2): below corner speed the radius stays the corner radius, at
corner speed the turn rate peaks, faster the radius grows with S^2; the velocity changes by at most a(S) / 20 a tick. The
controller: the fly zone first (soft: off the map with its aim behind, a full turn toward the aim; hard: at the fly zone's
edge, a full turn toward the map's centre, slowing toward corner speed); an aim behind the beam beyond two turn radii: a
full turn to its side; behind and closer: the lazy turn; ahead: a pure lateral turn onto the aim's bearing. *Quick turn
(E12, D-TURN1):* a fresh player order (Move, Attack, Attack-ground, AirTask, ReturnToBase, Rearm that changes its order,
task or homing; never Guard's own pick) starts `FixedWingState.QuickTurn` (hashed while set): for up to 2 s a max-g turn
toward the new aim (never the lazy turn) on idle with its speed brakes out (cruise less corner speed a second squared),
never below its corner speed, where its turn is tightest; over once the aim is within 10 degrees of its nose. A 180-degree
reversal from cruise now takes 1.6 s for the multirole and 1.65 s for the fighter (1.95 s before), overshooting 8.8 cells
instead of 10.5; the bank still rolls in at the client's roll rate. *Speed:* a tick changes S by (thrust(throttle) - drag x (S / dash)^2 - bleed x (a used / aG)^2 -
gravity x sin(climb angle)) / 20, within [minimum, dash]; each throttle's thrust is the drag at its equilibrium speed
(idle: the minimum; cruise; military: attack; afterburner: dash, `accel` its thrust) and the bleed is set so military
holds a level turn of `sustainedRadius` at cruise. *Throttle* (`Idle`, `Cruise`, `Military`, `Afterburner`; hashed): the
lowest whose thrust beats the resistance plus half the gap to the wanted speed a second; the afterburner only for a
dash (`FixedWingState.Boost`: evasion, egress) or a stall recovery. It wants cruise, attack speed on a strike, corner
speed at the fly zone's edge or in a quick turn. *Height:* the height planner (E12, below): its flight height (by map size
since E13, D-ALT1; `ingressHeight`
on a strike) plus `separation` x (ID mod 5) for wingmen over the terrain along `lookAhead` ticks of its path (5 s), and
`floor` over any roof there; it climbs at most `climbRate` (scaled by S / cruise; not at all at its minimum speed), descends
at half that following the ground (half its g of vertical acceleration), and a strike, a break or an engagement dives at
`diveRate` with its full g, never below the same profile at its `floor` (diving, plus what it sinks before its g stops
the descent).
*Fuel:* the throttle burns `burn.idle/cruise/military/afterburner` percent of nominal into an accumulator and the
lifetime system takes whole units off the lifetime (the fuel). *State:* `Unit.Wing` (speed, exact velocity, throttle,
lateral acceleration, load factor in thousandths of a real g, boost, evade, quick turn, the fuel accumulator), hashed while it flies
free. The loader refuses a sustained turn tighter than the max-g turn at cruise and derives a fixed wing's `turnRate`.
Test aircraft: `content/fixtures/fixed_wing_test.json` (`test_fixed_wing`, the multirole's numbers; tests and the air
bench only).

**The height planner** (E12; `Sim/FlightHeight.cs`; every aircraft in free flight, drones and jets). Before E12 the height
command was the flight height over the higher of the ground under it and one sample of ground 2 s ahead, chased at the
full climb or dive rate (bang-bang): the far sample swept sideways across the ground in turns and passed narrow features
(a building's pad, a ridge) before the aircraft, so it climbed, sank and climbed again, its vertical speed reversing in
one tick; structures were not seen at all. That was the owner's "bouncing off structures" (measured: crossing a row of
six buildings on 6 m mounds the multirole reversed its vertical speed 16 times, the fighter 20, the Watcher 7, with jumps
of up to 996 raw a tick squared, about 80 g; over the game's review map with towns 80 to 105 reversals per 1,000 ticks for
the jets, 25 to 35 for the drones; a strafing jet 5.4 m below a 16.5 m roof). Now: (1) a clearance grid, per 2 x 2-cell
block the highest terrain corner and, apart, the highest structure top on it, both dilated by a block (scratch, not
state: built when an aircraft first asks, kept current from the terrain's change journal and a diff of the structure list
in ID order; content without aircraft never builds it); (2) a profile along its path (its current turn continued) every 2
cells out to 5 s of travel: its flight height over the terrain there or its clearance over a roof there (jets `floor`,
drones 10 m), less what it can climb by then at three quarters of its climb rate; the highest is what it wants now: it
climbs early and holds over a row, and a jet at 60 m does not hop over a 16 m block; (3) the command tracks that with a
bounded vertical acceleration and the largest vertical speed it can still stop from (integer deadbeat: no overshoot), a
1 m hold band when level, half its climb rate down when following the ground (drones: full climb in 1 s). The terrain floor
stays as a counted last resort (`AirFloorClamps`, 0 in the tests; a drone off its launcher starts under it, and its lift
counts toward `VelZ` only up to its climb rate, so it is not carried on as a climb). After: crossing that row, one reversal (up, then down
after it), climbing from 10 cells before the first mound, vertical acceleration at most half its g (drones 8 m/s^2); on the
review map 15 to 18 reversals per 1,000 ticks for the jets (0 over flat ground with towns), 3 to 6 for the drones, all
smooth; strafing beside a row it stays 5.9 m or more over the roofs. Tests: `FlyZoneTests`.

**The fly zone** (E12, decisions D-FZ1 to D-FZ4; replaces E3's edge margin). Every aircraft may fly past the map's edge up to
`flight.flyZoneMargin` on every side (the fly zone: the map grown by it; 48 cells when content leaves it out, or twice its
turn radius plus a second of travel if that is more; the multirole 50, the fighter 55); ground units never leave the map.
The loader refuses a margin under twice the turn radius plus a second of travel (a fixed wing: its max-g turn at cruise).
Orders, targets, orbit centres, waypoints, areas and lanes stay on the map (`World.OrbitCentreOf`); an orbit, a racetrack
or a turn swings out over the band as it falls. The controller looks a second ahead (a fixed wing above cruise adds the
distance to slow to cruise at idle): *soft*, flying off the map with its aim behind it, a full turn toward the aim (it
prefers to turn over the map rather than the lazy law's wide swing); *hard*, past the fly zone less a turn radius plus a
second of travel on a side it flies toward, a full turn toward the map's centre (a fixed wing pulls to corner speed). No
countdown, no warning: it steers back. The step is clamped to the fly zone only as a last resort, counted
(`AirEdgeClamps`); the map's own clamp is never used for an aircraft (free flight, climb-out, pursuit points). Over the
band it follows the normal rules (D-FZ4): its sight is its own disc clipped to the map (the fog's air viewers keep their
true cell), it is spotted and shot at by slant range as anywhere. `World.FlyZoneMarginOf(u)` and `World.FlyZoneMargin`
(the widest in the content) tell a renderer how far out to draw ground and let the camera go. 10,000 ticks of random
orders (with dashes) for the Watcher, the Overseer, the scout drone and both jets on 96- and 192-cell maps, with fog:
they use the band, never pass the fly zone, the clamp never fires. The airfield's home check still keeps the pattern on
the map (unchanged). `edgeMargin` is retired (accepted, ignored). *Airfield steering (E12):* holding and on the final's join,
an aim behind the beam turns the aircraft at its full rate toward the aim's side (free flight's turning-home rule): a Watcher
that rolled onto the centre line past its join's entry point once flew on low over the runway and far beyond before it came
round (`AirfieldFlightTests.ADroneThatRolledOntoTheFinalPastItsJoinComesRoundAtOnce`).

### Detection, signatures and team cueing

- **Sensors.** `airVision` is a slant distance from the sensor's eye (an air defence weapon's launcher, a soldier's eye,
  a window, a flying aircraft's height): AA soldier 150 cells, air defence vehicle 260, multirole 110, fighter 160; the
  Watcher, the Overseer and the scout drone have none (the Overseer cues nobody: a night decision). A sensor
  detects a target within its air vision x the target's signature `detect` / 100. Under fog the same slant reach spots
  air units for its side (E11: it measured ground distance before, so the Overseer 250 m up was seen out to 150 cells
  of ground distance by a soldier who detects it only within about 83). Air targets are never hidden by ridges or woods.
- **Air classes are content** (`air_classes.json`: `ballistic` and `drone` carried by projectiles; `scout`, `watcher`,
  `overseer`, `fpv`, `multirole`, `fighter`, `attacker` (H2) by units). A class's value is its position (new classes go at the end); the
  interceptor carries everything per class: `killBest`, `speedRefs` (the jets judged against their attack speed, 23 to 24
  cells/s), `hitDamage` (the share of HP a successful roll takes; unlisted: a kill) and the Overseer's flat `hits` rule.
- **Signatures** (archetype `signature`: `detect`, `lock`, `kill` percents; 100 each without one): detection range x
  detect, lock time x lock (the tracking bonus counts from the lock), every kill or hit chance x kill. The fighter's
  50 / 200 / 60 (D7): an AA soldier sees it at 75 cells instead of 150, locks in 3 s instead of 1.5 and hits it at most
  21% instead of 35%.
- **Team cueing.** Each tick air defence runs, every alive, built unit with an air vision (not inside; a flying aircraft
  from its height) is a sensor for its player: what any of them detects is tracked for the side
  (`World.AirTracked(player, id)`; scratch, not hashed). A ground shooter may pick and lock a tracked target **within its
  own weapon's range** without detecting it itself (the lock time is its own); an aircraft on Guard may hunt a tracked
  target inside its zone and fires only within a weapon's range. No sensor gives map-wide awareness: tracking is
  bounded by the sensors' reach, firing by the weapon's.
- **The radar-cued lock (the mobile radar, 2026-10-22).** A sensor with a `cueLock` (a percent: the Resistance's mobile
  radar, 70) set up is a cueing radar: a ground air defence weapon of its side whose current target a cueing radar tracks
  needs only that percent of its own lock time (after the target's signature; the lowest percent when several track it;
  ticks truncated). Nothing else changes: the pick stays the team cueing rule above (within the shooter's own range), the
  kill chance is the shooter's own. Every other sensor, the radar truck included, has none (`Sim/Systems/RadarCueSystem.cs`;
  "Mobile radar" under "Resistance faction").
- **The battery link (the SA-6 launcher, 2026-10-22).** A ground interceptor with a `battery` block has no sensor of its
  own and is not cued by its side's team cueing: it picks only what a working radar of its side carrying the battery's
  `radarTags`, within its `linkRadius`, tracks itself; with `semiActive` the missile needs such a radar (counted from its
  launch point) to track its target every tick to the fuse (`Sim/Systems/BatterySystem.cs`; "SA-6 launcher" under
  "Resistance faction").
- **Counter-stealth, the radar horizon and the emitter (the S-400 type radar, 2026-10-27).** A sensor's `counterStealth` moves every
  signature value that percent of the way to 100 against it (its battery launchers lock and kill by the same); its
  `radarHorizon` hides air targets lower than `below` beyond `beyond` while the ground cuts its line to them (the one exception
  to "never hidden by ridges"); its `emitter` reveals it to enemy radars while it searches (`Sim/Systems/LongRangeRadarSystem.cs`;
  "S-400 type air defence" under "Resistance faction").
- **What the air defence vehicle reaches against the jets (E11, the jets demo's question).** Its radar detects the
  multirole out to 260 cells slant and the fighter out to 130; its missile reaches 220 cells (12 minimum; 45 cells/s for
  300 cells of flight, 70% at best against both jets, x0.6 for the fighter, a hit kills; chaff leaves 60% of it). On the
  game's 256-cell review map with the field 85 cells from red's camp, those 220 cells cover everywhere the demo's jets
  fly: that is why two crewed vehicles shot down every jet. By design of the content numbers (set for one-way drones
  at 12 cells/s), not a bug; the levers are under "Decisions to confirm". An uncrewed vehicle never fires (`CanAct`) but
  its radar still counts as a sensor, as an empty vehicle's eyes still see.

### Who can target whom (as built)

| Shooter / target | Ground units | Structures | One-way drones | FPV drone | Scout, Watcher, Overseer | Jets | Parked aircraft |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Rifles, rockets, artillery, missiles (ground weapons) | yes | yes | no | bullets 15% | no | no | blasts only |
| Technical's heavy MG (`airGun` with `ceiling`, D5) | yes | yes | no | bullets 15% | below 40 m | below 40 m | blasts only |
| AA soldier (Igla, IR) | no | no | yes | yes (70%) | scout 5%, Watcher 10%, Overseer hit 45% / 55% HP | 35%, a hit takes 60% | no |
| Air defence vehicle (radar) | no | no | yes | yes (85%) | scout 20%, Watcher 40%, Overseer hit 65% kills | 70%, kills | no |
| Jet radar missile (`aam_radar`) | no | no | yes | yes (90%) | 75 / 85 / 90% | 70% | no |
| Jet IR missile (`aam_ir`) | no | no | yes | yes (90%) | 60 / 75 / 80% | 75% | no |
| Jet gun (`airGun`) | strafing | strafing | yes, in its cone | yes (85) | 70 / 50 / 60 | multirole 30, fighter 25 | splash |
| Jet bombs (`delivery`) | yes | yes (x2) | no | no | no | no | splash (x1.2) |
| Watcher missile | yes | yes | no | no | no | no | no |

The percents are each weapon's best shot per class before the signature and the formula's other terms.

### Air-to-air (E6)

The air defence system runs every flying unit with an air weapon (an `intercept` missile or an `airGun`) after combat
and before the projectiles, so a missile fired this tick flies this tick. **Who engages:** only an aircraft flying free
(not at its airfield, not homing). On Guard with Aircraft or Drones in its filter: the nearest enemy air target of those
kinds (one-way drones, the FPV drone, scout, Watcher and Overseer count as drones; jets as aircraft) inside the area
plus its leash, that a weapon with ammunition can engage and that it detects or its side tracks; it keeps its target
while it may, prefers one no friendly missile flies at and no friendly lower ID tracks, and looks again twice a second
(staggered by ID). On an attack order at an air unit it engages only that one until it is down, landed, out of reach or
it goes home. An air target being engaged comes before a Guard ground pick. **Steering:** a lead pursuit (at most 1 s
ahead) at the target's height (never below its floor); within four gun ranges, the target ahead, it slows to the
target's speed plus 3 cells/s, so passes, overshoots and re-attacks come out of the flight model. E13 (D-PUR1): a pure
pursuit in the last second before gun range, speed brakes closing for the gun, and a break-off before it would fly
through its target (see "Attack runs, elements and pursuit").

**The gun** (`airGun`): within range (slant) and its `cone` of the shooter's 3D velocity, no lock: one burst (a round, the
reload), chance = `hitBase`(class) x crossing (`crossHalf` / (`crossHalf` + the line of sight's turn rate)) x range
(`rangeFar` percent left at full range) x signature kill; a hit deals the damage through `air_gun` (a drone dies to a
burst, a jet takes about four). Never decoyed. **Missiles:** tracking counts while the target is in the widest launch
cone (`boresight`) of its usable missiles; it fires the shortest-ranged missile whose range covers the target, once
locked (`lockTicks` x signature lock), inside that missile's cone, reloaded, with no friendly missile already at the
target. The missile is the air defence's (lead pursuit capped by its turn rate, max flight, a 3D proximity fuse, the kill
model x signature kill x countermeasures), at its own speed (the jet's is not added). One reload serves all an
aircraft's air weapons (a release a second); the gun is tried first.

**Countermeasures** (archetype `countermeasures`: `kind` flare or chaff, `salvos` a sortie, `vs` seeker, `pk`, `window`,
`perMissile`, `rearmTicks`): a hostile guided missile homing on it with a seeker a dispenser decoys, within `window`
ticks to go, gets one salvo (at most `perMissile` a missile): the missile's kill chance x `pk` percent. The stock
(`Unit.Salvos`, hashed) is per sortie, refilled only on a Rearm (at the station's rate, `rearmTicks` a salvo at 1x).
Multirole 8 flares and 6 chaff, fighter 4 and 4. **Evasion** (archetype `evade`: `breakG`, `warn`, `maxTicks`; a fixed wing
in free flight): a missile homing on it within `warn` ticks starts a break, once per missile: for at most `maxTicks` it
turns to the beam at `breakG` percent of its g, dives toward its floor, lights the afterburner; edge avoidance still
comes first. No roll: the missile's turn cap, lead law and fuse see the real geometry, so a well-timed break makes a
missile overshoot. Both jets: 100% g, warned 3 s out, at most 2.5 s (`FixedWingState.Evade`, `EvadeFrom`, hashed when
set). **The FPV drone** is an air target (archetype `airBody`: class `fpv`, 8 m over the ground, its step toward its goal).
**Kill attribution:** `Unit.KilledBy` (`Gun`, `Missile`, `Fuel` when it ran dry in the air, `Crash` from a runway crater,
else `Blast`) and `World.AirLosses`.

### Air-to-ground (E7)

Any aircraft whose weapons carry a `delivery` block (a bomb) or an `airGun` that reaches the ground. **Who attacks:** an
attack order at a ground unit or an attack-ground order at a point (the Watcher fires its missile at a point too:
outcome `Point`); Guard with ground kinds in its filter picks the nearest spotted enemy of them inside its area plus its
leash. A strike ends with the target dead or inside something, out of its side's sight for 10 s while it is within its
sight of where it was last seen (it attacks there meanwhile; E13: the count no longer runs while its runs take it away),
out of every weapon that hits it (after the last pass's break and egress), another order, or a trip home; a player's
order that ends without a hit says why (`World.OrderRefusals`, below). **The weapon of a pass:** the first
bomb with ammunition that can hit the target, else the gun (strafing); a point is bombed, then strafed.

**A pass** (`Unit.Strike`, a `StrikeRun`, hashed while present) is the attack-run state machine (E13, below): *Ingress*
-> *Align* -> *Run* (from the initial point, `ipDistance` before the release point, plus its capture) -> release ->
*Break* -> *Extend* -> *Reposition* -> *Align* again while ordered and armed, or *Egress* after its last pass.
**Profiles** (`delivery.profile`): *level* holds the release height from a pop-up; *dive* climbs to the roll-in
height (release height plus the IP distance up the dive line) and flies down a line at `diveAngle`, releasing only while
diving; *toss* runs in low (three times its floor, 30 m) on the afterburner, pulls up a second before its release distance and releases climbing; a
*guided* bomb (`delivery.guided`) is released level once the target is within its reach and 30 degrees of the nose,
rolls its hit at release (`hitIdle` / `hitMoving`) and glides at the target (hit) or `missNear` to `missFar` beside it.
**The release solution:** each run-in tick it predicts where a bomb released now would land by the bomb's own integer
fall, leads a moving target, and releases when that point is within half a tick's travel of the aim point; past it by
more than the release window (`attackRun.releaseWindow`, 2 cells; plus the profile's scatter off its line) it aborts and
comes round again. E13: a dive follows its line a push-over ahead (the travel while its full g builds the dive's sink rate,
halved) and releases only once sinking at half the dive's slope, so it releases diving near 60 m, not at the roll-in. Scatter (`scatterLevel` /
`scatterDive` / `scatterToss`) is seeded and applied to the release velocity; a `stick` releases its next bomb each
`stickSpacing`. **The bomb** (`Projectile.Bomb`, hashed while present) falls at the delivery's `gravity` (70 m/s^2,
compressed like the speeds), goes off on the ground or the first roof, vehicle or soldier on its way down: a direct hit
through the `bomb` warhead (x2 against buildings), the splash, the runway effect (2,000 lb: a large crater; others small).
A 35-degree dive released at 60 m lands about 19 cells on, a level drop about 26, a toss about 38. **Strafing**
(`airGun.strafeRange` 18 cells, `strafeEvery` 5 ticks): a 12-degree dive line (its floor keeps it at 10 m), each burst a
1-cell splash walking from 2 cells short to 2 long with a half-cell seeded jitter; about 3 bursts a pass. **Airstrike
tiers** for visuals: `World.StrikeTier(damage)`, 1 to 7 (T2 350, T4 700, T6 1,300).

### Attack runs, elements and pursuit (E13)

The owner rated the AI-flown jets 5/10; E13 follows the air-feel research's top ten (`rts-work/air-feel-research.md`).
Code: `Sim/Systems/AirStrikeSystem.cs` (the state machine), `Sim/Systems/ElementSystem.cs` and `Sim/Wingman.cs`
(elements), `Sim/Systems/FixedWingSystem.cs` (pursuit separation); suite `AttackRunTests`.

**The attack-run state machine** (`StrikePhase`; geometry per class in archetype `attackRun`, zeros derived):
- *Ingress*: toward the target at its ingress height until within the run line's length (release distance + IP distance
  + `align`) with the aim within 45 degrees of the nose; inside half an align of the IP it extends away first.
- *Align*: the run line is locked from the aim point through where it is (the entry heading, chosen before the run: the
  turn-in is behind it), turned by (ID mod 5 - 2) x `offset` / 2 degrees so aircraft given the same order never stack;
  it flies a carrot a turn radius (at least a second) down the line, wings level, and climbs or descends to the profile's
  height (a dive's roll-in, a level release height, a strafing line); within the IP plus its capture: the run.
- *Run*: the E7 profile and release solution. Release, last burst or abort: a pass counted, the break.
- *Break*: at least `breakTurn` degrees (60) and `break` of travel (46 cells: 2 s) to its side (the first time toward
  the map's centre, then the same side: passes alternate direction through the target, a figure of eight), the
  afterburner for 2 s, climbing to its flight height.
- *Extend*: straight on for at least `extend` (92 cells: 4 s) plus (ID mod 3) half seconds and until it is a run line
  plus a turn diameter from the target, or until flying on would bring it within a turn diameter plus a second of the fly
  zone's edge (the turn-in then falls in the band beyond the map).
- *Reposition*: a full turn toward the target (the fly zone's rules first) until it is within 8 degrees of the nose: the
  next align (closer in than its release distance plus its capture: out again).
- *Egress*: out of everything that hits the target after a pass, the break and an extend away, then the strike ends
  (its task resumes, or it orbits: the owner's rules; no automatic trip home for ammunition, except a planned pass
  that empties every stock: decision 143).
Measured (`AttackRunTests`, a 320-cell map, 4 x 500 lb at a point mid-map): pass gaps 14.8, 15.2 and 13.9 s (the
research's 12 to 25 s); run lines locked 105 to 143 cells out; the run begins within 0.9 cells of its line; every bomb
released diving at about 57 m, 19 to 20 cells short; then strafing passes until the gun is empty, then the egress.
On a 240-cell map the fly zone's band cuts the extends (gaps 9 to 13 s).

**Elements** (archetype `element`): fixed wings of one kind given a new order or task by the same command (Move, Attack,
Attack-ground, an AirTask) form elements of at most one more than their slots (the jets: 3 slots, so 4; sizes balanced,
5 make 3 + 2), the lowest ID leading; `Unit.Wingman` (lead, slot, the lead's recent path; hashed while present). A
wingman flies the lead's own path (its positions a delay ago, offset `aside`: never a point fixed to the lead's nose,
which would swing it across the lead's turns): the delay is the slot's `back` at cruise speed; it finds its place on that
offset path (the nearest recorded point), steers a second ahead of it and trims its speed by its distance from the slot
(half the gap a second, between halfway to its corner speed and its dash); within 3 cells of its lead it turns away and
slows (the backup). The element holds while both fly free on the same order and task; otherwise the wingman flies its
own order until they match again; a lead lost hands the element to the lowest ID of its wingmen; a new order to one of
them takes it out (a Shift-appended waypoint does not). On Guard the wingman takes its lead's pick. On a strike the delay
is `stagger` x its slot (3 s): it flies the lead's path 3 s behind, begins its own run where its record shows the lead
beginning its run (on a line through where it is: an offset line), releases by its own solution, breaks the lead's way
and takes its slot again. Measured: a pair on a racetrack patrol, slot error mean 6.8 cells (most of it in max-g turns,
where the inside slot is tighter than the wingman can turn), at most 14.6, never nearer its lead than 10.5 cells; a
pair's four bomb runs each 2.9 to 3.3 s apart, breaking the same way.

**Pursuit separation** (archetype `pursuit`, D-PUR1; row 67): a lead pursuit on the approach and a pure pursuit within a
second of its gun's range and inside it; closing for the gun within four gun ranges it slows to the target's speed plus
3 cells/s with its speed brakes out (the quick turn's rate); when their closest approach (both flying on, 3D) would come
within `separation` (6 cells) in the next `breakOff` (2 s) it breaks off: a hard turn away from the target's side while
the target is ahead of its beam, then straight away on the afterburner, `climb` (10 m) above the target, until
`reattack` (30 cells) or `maxTicks` (6 s); then it attacks again. The jets' gun reaches 15 cells (was 9) so a gun pass
fits before the break-off. Measured (5 seeds each, missiles emptied): fighter and multirole against the Watcher, fighter
against the Overseer: closest approach 6.6 to 7.1 cells (was 0.4: it flew through), gun kills 4 of 5 in each.

**The break with flares** (D-BRK1; row 64): a fixed wing with an `evade` block breaking against a missile dispenses at
once (its launch warning, 3 s out, instead of 2 s) and that salvo leaves the dispenser's `breakPk` (flares 30%, chaff
35%) instead of its `pk` (55%, 60%): it turns away as the decoy blooms. Measured in `AirToAirTests`' Igla geometry (four
start points): break and flares 2.9 to 3.2% against flying straight with flares 7.8 to 8.7% (at E13's heights; before:
6.3% against 5.7%, the break was worse).

**Orders are never dropped silently** (D-ORD1): an aircraft refuses or gives up an attack order only with a reason in
`World.OrderRefusals` (`OrderRefusal`: aircraft, player, `OrderRefusalReason`, target, cell; the tick's list, read after
`Step`): `NoAmmo` (every stock empty: it rearms only on a Rearm order), `NoWeaponForTarget` (ammunition, but nothing that
hurts that target: say only its air-to-air missiles), `RefuelTrip` (its automatic trip home: refused while holding or
landing, given up if bingo comes mid-strike), `Homing` (holding or landing on a Rearm or ReturnToBase order),
`TargetLost`, `NotVisible` (a strike drone at a target its side does not see), `Bingo` (D-FUEL1: refused, its fuel will
not take it there, through a pass of 20 s and home with its reserves; or a strike ended by bingo), `TargetDead` (the strike
ended: the target is destroyed), and two notes on orders it took: `Unseen` (flying to the last known cell) and
`Rearming` (D-ORD2). A strike that ends after its last pass says `NoAmmo` (none of its weapons for that kind of target has
ammunition) or `NoWeaponForTarget`. Found in practice: an Attack at a fogged
target was dropped at the command (no unit got the order), a strike that ran out of bombs and gun ended at once, an
Attack while holding or on final was idled, a bingo mid-strike ended it, all without a word. Now an aircraft sent at a
target its side does not see flies to the clicked cell (or the target's last known cell: a structure's ghost), circles
it and strikes once it is seen (by itself or its side); 10 s circling there without seeing it: `TargetLost`. Bombs gone,
it strafes with the gun when that can hurt the target (E7's weapon rule). A player's Attack clears Guard's pick: Guard's
own targeting waits until the order is done. The attack order keeps the target's last known cell (`Order.Cell`).
*D-ORD2:* a jet parked at home (or rolling out, taxiing in) given an Attack or Attack-ground order it has no weapons for,
whose full loadout has one, takes it as an implicit Rearm (still the player's command, so "rearm only on command" holds):
the full service at its station, then it launches and attacks (`Rearming`); any attack order from the ground waits for its
service (a full tank) and ends a Return-to-base hold; Attack-ground from the ground was dropped before (only Attack was
kept). An airborne jet without a weapon for the target refuses (`NoAmmo` / `NoWeaponForTarget`): it does not go home.
Reproduced (a 1,024-cell map, before E13): an Attack at a fogged target was dropped at the command; a jet 120 s aloft
was at bingo and went home at once; one 70 s aloft took the order and turned home at bingo 19 s later, before its run;
a parked jet without bombs ignored Attack and Attack-ground (both idled). After: all taken and flown (hits 9 to 29 s
after the order), a jet at 15% fuel is refused with `Bingo`, the parked one rearms, launches and attacks.
**Endurance and transit speeds by map size** (D-FUEL1, D-SPD1; flight `enduranceByMap`, `speedByMap`): the jets' tank is
their lifetime up to 512-cell maps, rising linearly to 200% at 1,024 cells (7 min for the multirole, `Unit.Tank`; a refuel
fills it); their cruise and attack speeds rise to 135% and their dash to 120% (multirole cruise 20 to 27 cells/s, attack
23 to 31, dash 28 to 33.6; the envelope's thrusts re-derived, `ScoutFlight.On`, `World.FlightOf`), and their fly zone
widens to twice the max-g turn at that cruise plus a second of it (multirole 50 to 78.8 cells, fighter 55 to 84.8;
`World.FlyZoneMarginFor`; `World.FlyZoneMarginOf` stays the content's). A strike's turns and runs (Reposition, Align,
Run, Break) keep the content's attack speed (23 / 24 cells/s) with speed brakes from the faster transit, so runs, turn
radii and their look are as on small maps; its transit (Ingress, Extend, Egress) flies the faster of that and the map's
cruise. Crossing 900 cells of a 1,024-cell map: 32 s (45 s at 20 cells/s before); a strike on red's garage 950 cells from
blue's field in `--demo=bigsky` releases at tick 674 (954 before). The fly-zone fuzz runs on 1,024 cells too.

**Flight heights by map size** (D-ALT1; flight `heightByMap`): the jets cruise and patrol 140 m up (the fighter 168) on
maps up to 512 cells across (the longer side), rising linearly to 280 m (336) at 1,024 cells and on at that slope, never
above 320 m (384); `World.FlightHeightOf(u)`. They run in at their ingress height, 70 m (80): the align comes down to the
profile's height (a dive's 82 m roll-in, a strafing line from the align) and the break and extend climb back to cruise,
all through the height planner. The climb-out climbs to the ingress height (free flight climbs on). Drones keep theirs.
*Balance:* at 140 m (70 cells of slant straight up) an Igla (140-cell range, 150-cell sight) reaches a multirole within
121 cells of ground distance (137 at 60 m); the fighter (detected at half range, 75 cells) is never detected by an AA
soldier at its cruise height, only cued by a radar; at 280 m (a 1,024-cell map) an Igla reaches a cruising jet only
almost overhead. MANPADS now threaten jets mainly in their attack runs (70 m and below), as intended; the heavy MG's 40 m
ceiling only reaches strafing passes; the air defence vehicle (220 cells) is barely affected. *Airborne sensors* (D-ALT1b): a jet's radar
measures its air vision as a level distance (looking down costs it no range), ground sensors keep the slant.

### Losses: crash and ejection (E10)

What a jet leaves when lost in the air is content (archetype `loss`; drones have none and simply disappear).
`Unit.KilledBy` decides: a **gun kill** (a jet's gun or the technical's heavy MG) makes the pilot eject and the empty jet
fall as a wreck; **any other kill** (missile, blast, fuel) sends the wreck down and nobody ejects; destroyed on the ground
it leaves neither; recovered at its station it is no loss.

- **The wreck** (`World.Wrecks`, ID = the aircraft's, hashed while any falls): from the death tick's cleanup with the
  jet's position, height and exact velocity; it falls at `crash.gravity` (40 m/s^2) losing `drag` (10%) of its
  horizontal speed a second, never steering. A missile kill breaks it up with `breakUp` chance (40%): then `breakUpDrag`
  (50%), a steeper fall (`Wreck.BrokenUp`). From 60 to 80 m: about 30 to 37 cells on in under 2 s at about 66 degrees
  (broken up: 22 to 26 cells, 79 degrees). It goes off on the ground or the first ground unit's top on its way down: a
  hit and a splash through the `crash` warhead (D9b: 150 damage, 3 cells, half at the edge; friends too; nobody blamed)
  and a small crater on a runway (`crash.runway`).
- **The ejection** (`World.Chutes`, hashed while any comes down): the seat throws him `seatRise` (40 m) up in `riseTicks`
  (1 s), then `chuteRate` (6 m/s; the main canopy at `chuteTicks`, 2.2 s), drifting along the jet's heading at `drift`
  (1 cell/s) for `driftTicks` (3 s; no map wind). He is **not a unit** while he comes down (untargetable, D8). He lands on
  the nearest cell a soldier can stand on within 16 cells as an idle `eject.unit` (the `pilot`: 45 HP, a compact rifle)
  of the jet's owner; with nowhere to stand he is lost. About 18 s from 60 m.
- **The heavy MG against low aircraft** (D5): a ground weapon whose `airGun` has a `ceiling` (40 m) engages enemy
  aircraft of its classes flying lower than that over its ground, on the AA soldier's stance rules (guarding, standing,
  crewed): turret on the nearest within its sight or range, a burst through the air-to-air gun's roll (jet 50%, fighter
  40% before its signature, drones 30 to 40%; `crossHalf` 3 rad/s; down to half at its 6-cell range), 12 damage (`bullet`
  does 100% to `aircraft`): it finishes hurt jets (about one hit in eight strafing passes); a killing burst ejects the pilot.

### Airfield operations

The M2 and M3b rules (hold, land, taxi, park, service, take off; craters) apply to every aircraft; the air-combat
milestones added (and since 2026-10-24 every airfield leaves 30% and returns 50% faster, with an approach sequence: "The
faster airfield"):

- **Service rules (E2)** as above: refuel-only automatic stops on an open stand, Rearm's full service at a loading
  shelter, RTB holds. Countermeasure salvos refill only on a Rearm.
- **Production (E8, D9):** the airfield facility's `production` (`tags: ["jet"]`, `bays` 1, `queueMax` 2, `intoStation`)
  builds a jet in its build time and parks it in a free station (an open stand first), full of fuel and stocks, held
  until its first order; with no free station it waits in the facility, so stations limit the number of jets. It builds
  only jets whose class the airfield serves and whose pattern fits the field.
- **The overhead break (E9, D17; both jets; drones stay straight-in):** cleared from the hold, the jet joins its initial
  point (`initialDistance` 40 cells beyond the approach end, on a join circle 5/4 of its tightest turn at cruise), easing
  to `breakSpeed` (14 cells/s) and `patternHeight` (40 m); runs in down the centre line and at midfield **breaks** into a
  level half turn of its pattern radius (5/4 of its tightest turn at break and approach speeds: multirole 10 cells,
  fighter 11.25) away from the rows (over them when only that side fits); the **downwind** leg two radii out (gear
  down), slowing to approach speed; from the **perch** a descending half turn (the **base**) onto a short final a turn
  radius before the fix (`fixDistance` 12 cells, 12 m); then the straight-in's glide, flare and touchdown. The go-around
  check is at the fix; a crater cutting its stretch at any point sends it round. In every air phase at the airfield a
  fixed wing turns within a(S) (since the faster airfield, 2026-10-24, at the return tempo k: a(S / k) k^2, the same circles
  flown sooner). **Aerobraking:** after touchdown the nose stays up until `aerobrakeSpeed` (6 cells/s; times the tempo).
- **The takeoff rule (E9):** with `speeds.militaryAccel` (1.2 cells/s^2) it rolls on military power when its military roll
  (34 cells) fits in half the open stretch ahead of its line-up connector, else on afterburner (`takeoffAccel` 2.0: a
  20-cell roll; `AirOps.Afterburner`, hashed). On a 4-segment field both jets take off dry. **Climb-out:** straight ahead
  on the takeoff throttle within its envelope, free flight once the runway lock is released (2 s) and it has its corner
  speed: about 20 to 25 cells past the runway's end.
- **The home check: the pattern on the map (E9, E11).** Placement keeps the type's 30-cell corridor beyond each end, so
  every field stays valid; instead each aircraft checks its own need when it picks a home. A jet's `corridorNeed` (60
  cells: the initial point, its join turn and the climb-out) beyond the approach end (the runway's width), one of its two
  join circles and the break, downwind and base box on one side must lie on the map. A drone flying straight in has its
  join's turn circle checked (E11; derived, no content number): the circle (its entry point twice the larger of 10
  cells and its approach turn radius beyond the fix; radius 1.5 approach turn radii) on one side of the centre line must lie on the map with its
  `JoinBand` (the join's edge-avoidance band at approach speed: turn radius plus a second of travel; Watcher 14.6 cells,
  Overseer 11.7) to spare on its far and outer sides: about 79 cells beyond the approach end and 43 to the side for the
  Watcher, 73 and 35 for the Overseer. The join side is picked by the same rule. Before E11 a drone whose circle reached
  into that band circled on its join for good (a sweep of 480 arrivals at fields near the edges: 250 never landed, none
  since). Where a need does not fit at the runway's end but does a whole cell or more in, the aircraft uses a **displaced
  threshold** while its minimum landing run remains; a field that never fits is never its home (with no other home it
  orbits on until it runs dry) and its facility does not build it.
- **Craters:** rolling into a crater that brings it to 0 HP, a jet dies with `KillCause.Crash` (on the ground: no wreck,
  no ejection).

### Main air base and radar (H1)

Milestone H1 of the game repo's `docs/heavy-air-plan.md` (2026-10-21 night; the shared layout contract
`H1-layout.md`, 1 cell = 2 m). Content: `airfields.json` (type `main_air_base`), `archetypes/airbase.json`; code:
`Sim/Systems/AirfieldSystem.cs` (the core), `AirfieldDamageSystem.cs` (target pieces, rubble, rebuilds),
`AirfieldServiceSystem.cs` (service with the command building down), `ProductionSystem.cs` (the hangar's queue),
`VisionSystem.cs` and `AirDefenseSystem.cs` (the radar), `AirBaseSystem.cs` (queries); tests `MainAirBaseTests`.
Every number below is content unless marked (code); the decisions are rows 78 to 94 of "Decisions to confirm".

- **The type.** `main_air_base` serves S to XL. Its pieces: the core `airbase_core`, the facility `airbase_command`
  (the command building), the hangar `airbase_hangar`, the radar `airbase_radar`, the heavy bay `airbase_bay`, the
  runway segment `airbase_runway`, and the forward airfield's row, shelter and stand (shared pieces: a row,
  segment or station archetype may serve several types in the same role). Its grid: segments of 20 cells,
  runway 15 wide, grass 7, taxiway 10 (32 deep), a connector on every 4th segment (`runway.connectorEvery` 4:
  x 10, 90, 170 on the built-in runway, then 250, ... on extensions), 30 cells of corridor beyond each end; one
  column of rows from local (148, 34) (`rows.origin`), at most 12 rows (`rows.max`: the 4 built-in and 8 more);
  holding 40 cells out, 30 round (30 out, 24 round since 2026-10-24), from 60 m in 20 m steps, 6 places; wear patches of 5
  cells, at most 24 craters.
- **The core** (180 x 86 cells, 120 s on one bar, placed by any builder: the bulldozer). It founds the airfield
  (`Airfield.Core` = `Airfield.Id`) and is the pavement under the whole base: never a target, never damaged.
  Placement: quarter turns; its whole footprint free and within 4 m of one pad height; both runway corridors on
  the map; no other ground units where its three buildings will stand (builders excepted); every piece it lays
  in the owner's faction. While it is a site nothing of the base exists. Finished, it lays on their slots, all
  finished: the 9 runway segments, the command building (local (112, 46), 30 x 26), the hangar ((4, 44), 104 x 26)
  with its 4 heavy bays (bay i at x 4 + 26i, 26 x 26), the radar ((112, 74), 12 x 12) and rows 0 to 3 with their
  8 stations (4 shelters, 4 stands). Everything turns with the core's facing like the forward airfield.
- **Heavy bays.** A hangar's bays are rows of the airfield with a negative column (`AirfieldRow.IsBay`, column
  -1 - bay index; their unit is the hangar's), each with one station of kind `heavy` (`airbase_bay`: classes L to
  XL only, `minClass` / `maxClass`). Shelters and stands take S and M only, so a heavy parks only in a heavy bay
  and a jet or drone never does; a forward airfield (classes S and M) never serves L or XL. A bay's lane node is
  on the apron 6 cells in front of its door (joined to the taxiway); the heavy taxis in nose first to the bay's
  centre and leaves by pushback. Bays rearm and repair at 2x and refuel at 1x, like shelters. The heavies taxi at
  5.5 cells/s (3.5 in turns) and push back at 3 (content; decision 142): order to airborne from a bay in about 39 s
  (the attack jet) to 43 s (the gunship, the bomber), was 89 to 99 s (`HeavyBayTests`); 29.9 to 32.5 s since the faster
  airfield (2026-10-24, below). `World.HeavyBays`,
  `World.FreeHeavyBays`.
- **Landing at the base** (decision 144). The type's `minTaxi` 5.5 and `minTurn` 3.5 cells/s are floors on every
  aircraft's taxi speeds here (the heavies' speeds: a jet taxis 5.5, not its own 3.5, on the base's wide taxiways, and
  rolls out to its exit at that speed; at a forward airfield it keeps its own), and `breakDistance` 30 cells puts the
  overhead break 30 cells in from the threshold, not at midfield (90 cells in on the built-in runway). The final (the
  join to touchdown) from 37 to 39 s down to about 25 s for the jets, 55 to 40 s for the gunship and the bomber; a
  multirole from touchdown to parked at a row's stand 66 to 41 s (its exit is the x 90 connector, 80 cells down the
  runway, its row 58 cells back); the heavies' bays stay at 27 to 28 s (`HeavyBayTests`). Since the faster airfield (2026-10-24,
  below) the final takes 17.1 s for the jets and 25 to 28 s for the gunship and the bombers, touchdown to parked 26.9 s for
  the multirole at a row's stand and 17.8 to 18.4 s into a heavy bay. Content (the three fields
  are zero on the forward airfield: its own speeds, the midfield break).
- **Production.** The hangar produces every Sentinel air unit (tags `jet`, `drone`, `aircraft`: the Watcher, the
  Overseer, both jets; the heavies when they come) with a queue like the vehicle garage's (2 at a time, 10
  orders), into a free station (a stand, then a shelter, then a heavy bay; the first that takes the aircraft's
  class and span), held there until its first order, `AirOps.Since` the tick it rolled out there (a renderer opens a
  heavy bay's door for it; decision 145); with none free the finished aircraft waits in its bay. An
  aircraft only heavy bays take is queued only while the base has more free heavy bays (standing, nothing parked
  or bound there) than the hangar has such aircraft queued or building. It produces only while the hangar stands
  and the command building stands (paused, the queue kept).
- **Target pieces.** The command building, the hangar and the radar are ordinary targets with the forward
  airfield's two stages: at 0 HP rubble at 50% of max HP, repairable by builders (one: the whole bar in half the
  build time), hit to 0 again gone, then rebuilt on its slot by `Build` (`World.AirfieldFacilityCell`,
  `AirfieldHangarCell`, `AirfieldRadarCell`; 60, 90 and 30 s). The command building down (rubble or gone): the
  hangar stops producing, the shelters and heavy bays stop rearming and repairing, every station still refuels
  (type `facilityDown: "refuel"`; the forward airfield gives nothing then), aircraft still land; rows and runway
  segments can't be added; the radar is not affected. The hangar is one target: its rubble closes all 4 bays (the
  heavies parked there take the 40% collapse damage and wait, as at a fallen shelter) and stops production; gone,
  its bays go with it and a rebuilt hangar lays them again.
- **Linking.** Any own airfield serves (refuel, rearm, repair) every aircraft whose class it takes: an
  aircraft is never bound to the field that built it (the home choice and Rearm / ReturnToBase targets were
  already per owner). A forward airfield whose facility slot lies within 40 cells (`link`) of the base's apron
  band (local y 32 to 44 along the core) is linked to it (`World.AirfieldLinks`, both ways) and **joins the base's
  taxi graph**: the base's graph holds the linked field's taxiway (as lane mouths), lanes and stops, joined by a
  link edge between the closest pair of their taxiway nodes (`World.AirfieldLinkTaxiways`: its two ends, for the client to draw that joining taxiway). So an aircraft
  of the base (landing there, or built by its hangar) may take a linked field's station (it is counted in the
  base's free stations for the home choice and production), taxis there over the link and back, and lands and
  takes off on the base's runway; the linked station serves it by its own field's rules (a shelter needs the
  forward facility). The join is one way: the forward field's own aircraft taxi only on their own field (they use
  its runway; they may still be sent to the base). A station holds one aircraft whichever field it came by.
- **The radar.** A structure with `groundRadar` and an `airVision` (`airbase_radar`: air 300 cells, ground 80,
  sight 60; the standalone `radar_site`: 200, 60, 40, built by any builder in 30 s, 12 x 12 cells, the same model).
  *Air:* its `airVision` from its top (its height, 8 m), a slant distance scaled by each target's signature
  (detect), as every air sensor: it feeds the side's air tracking (`World.AirTracked`, the team cueing AA and
  jets use) and, under fog, spots air units; a radar structure collects the side's cues even with no air
  defence shooter about. *Ground:* every enemy ground unit within its ground reach (a level distance from its
  centre) is spotted for its side: vehicles anywhere (no ridge or wood hides them; grounded aircraft count as
  vehicles), soldiers only on a cell without forest (woods still hide them); never structures (they need sight)
  or hidden units (traps need detection). *Sight:* its `vision`, the ordinary structure viewer (line of sight from
  its top; a structure's radius counts from its footprint's edge, so 60 cells out from the edge, 66 from the
  centre). As rubble it is off: no sight, no air, no ground reach (any rubble structure but a tunnel entrance now
  sees nothing: the forward facility's rubble too). `World.RadarOf` (on, the three ranges) is for range rings and
  the radar's animation (client only: no sim state).
- **The sandbox.** `SandboxSpawn` of `airbase_core` lays a finished base where asked (facing a quarter turn).

### The faster airfield (2026-10-24)

The owner (2026-10-24): "Let's make all aircraft get in and get out 30% faster, right now it's too slow. Also, when a plane
wants to get in the airway it's waaaaay too slow, planes wait for each other to enter and keep orbiting, just make the
planes return faster to the airbase", then "returning is faster than leaving". Content: `airfields.json` (every type's
`tempo` and a tighter `holding` with its `reach`), the helicopters' `rotary.tempo` (`archetypes/sentinel.json`); code:
`AirfieldFlightSystem.cs` (`TempoOf`, `Pace`, `Taxi`, the phases, the approach sequence: `MayLeaveJoin`, `JoinsLeft`,
`AirfieldTraffic`), `AirfieldServiceSystem.cs` (`RollCheck`), `HeliFlightSystem.cs`; tests `AirfieldTempoTests` (the old
pace measured in the same world by a content edit), `HeavyBayTests`, the airfield suites. Decisions 361 to 372.

- **The tempo** (content: `tempo: { "leave", "return" }` on an airfield type, percents; 130 and 150 on all three types; left
  out or 100: the aircraft's own numbers). *Leaving* (the pushback, the taxi out, the line-up) is time-scaled: speeds times
  1.3, the acceleration, the roll-out's deceleration and the brake times 1.69: the same ways, sooner. The *takeoff roll*
  accelerates 1.3 times as hard to the same lift-off speed (the multirole's military roll 26 cells, 34 at its own pace); the
  dry-or-afterburner choice is still made on its own roll (unchanged). *Returning*: the flight home and the hold (at 1.5
  times its cruise), the join, the overhead or straight-in pattern and the final (speeds, climb and descent times 1.5; the
  turn a tick and the easing times 2.25: the same circles and legs, flown sooner, so every pattern size, corridor and
  pattern-on-the-map check is as before), the roll-out, the taxi in and the park-in (time-scaled as leaving; the exit
  connector is the same). Unchanged: the climb-out and free flight, combat, fuel burn, bingo (it still counts the flight
  home at cruise: it goes home where it did and arrives with more fuel in hand), the runway lock's 2 s after wheels-up,
  the lane wait, the pivot from a standstill. `AirOps.Speed` is the paced speed it moves at; `AirOps.Pace` (percent,
  hashed) is its phase's pace for renderers (and the speed brakes' thresholds).
- **The approach sequence** (the owner: the next aircraft lands as soon as the one ahead is down and off the runway). Before,
  the head of the landing queue took the runway at its clearance and kept it through its whole pattern and roll-out (about
  25 s plus 14 s for a jet at the base), so the next waited in the hold: one landing every 40 to 45 s. Now the airfield's
  approach sequence (`Airfield.Approaches`, hashed while not empty) holds the aircraft cleared to land, in order, until
  touchdown: the head of the landing queue, at the hold, is cleared as soon as every aircraft cleared ahead of it has left
  its join (whoever holds the runway), so at most one waits on its join; it leaves its join for the runway (the overhead's
  initial point, the straight-in's entry point) only when it is first in the sequence (the one ahead is down) and the
  runway is free (it takes it there), or held by the landing aircraft ahead still rolling out (it takes it as that one
  turns off); else it circles its join. At its fix it must hold the runway, else it goes around (none in the tests).
  Departures get the runway only while it is free and nobody is cleared to land (landings first, as before). The next
  jet touches down about 16 to 18 s after the one ahead (its run in from its initial point).
- **Tighter, closer holds.** The forward airfield holds 16 cells round a point 20 cells out (was 25 and 25), the main air
  base and the forward airbase 24 round 30 out (was 30 and 40), heights as before; each is still wider than the widest
  turn at cruise of what it serves (the fighter's 15.4 cells, the strategic bomber's 23.4). The new `holding.reach` (the
  head of the queue is cleared within it of the hold point; left out: twice the radius) keeps the clearance distance as it
  was (50 and 60 cells): cleared within twice the new radius, the strategic bomber reached its join inside the join's
  circle and went round it (its join 11 s instead of 3).
- **In parallel.** Each airfield has one runway, so its landings are one at a time; the stations and taxiways were already
  used in parallel (each aircraft reserves its own station; an aircraft waits only at a lane another is leaving), and a
  base's linked forward airfield keeps its own runway and queue. Not built (not in the plan): sending a base's aircraft to
  a linked field's runway when the base's queue is long; intersection departures (the base's jets taxi 140 cells west to
  the first connector to take off: most of their 34 s).
- **Helicopters** (content `rotary.tempo`, 130 and 150 on all three): leaving its pad, its rotor spools up and it lifts off
  1.3 times as fast; recalled, its flight home to its pad (its cruise, its body speed limits, climb and descent times 1.5,
  its acceleration times 2.25; its yaw rate unchanged) and its descent onto the pad 1.5 times as fast. They never queue
  (one to a pad).
- **Measured** (`AirfieldTempoTests`, `HeavyBayTests`; the old pace's numbers are a7a2ef5's to the tick):

| | Leaving: order to wheels-up | Returning: recall to parked |
| --- | --- | --- |
| Multirole, the main air base (a row's stand) | 45.0 -> 34.0 s (1.32x) | 73.8 -> 49.2 s (1.50x) |
| Multirole, a forward airfield | 23.2 -> 17.6 s (1.32x) | 69.8 -> 46.4 s (1.50x) |
| Strategic bomber, the main air base (a heavy bay) | 42.5 -> 32.1 s (1.32x) | 73.9 -> 48.8 s (1.51x) |
| Attack helicopter, a helipad (its rotor run down) | 4.7 -> 3.6 s (1.29x; to flying) | 13.3 -> 9.1 s (1.46x; to down on its pad) |
| Light helicopter, a helipad | 3.6 -> 2.9 s (1.25x) | 11.3 -> 7.9 s (1.43x) |

  Four multiroles recalled together to the main air base: all parked after 200.5 s before (73.8, 113.0, 158.2, 200.5),
  101.6 s now (49.2, 68.0, 84.9, 101.6; touchdowns 22.3, 39.8, 55.5, 71.0 s), no go-around, the runway never idle while
  the next waits at the hold. A strategic bomber, a multirole, a gunship and a fighter recalled together: all parked in
  116.3 s without a go-around; four drones straight in at a forward airfield likewise (118.8 s). Out of a heavy bay every
  heavy is airborne in 29.9 to 32.5 s (was 39 to 43).

### Ground attack jet (H2)

Milestone H2 of the game repo's `docs/heavy-air-plan.md` (section 1.1, the A-10 type; 2026-10-21 night). Content:
`archetypes/sentinel.json` (`attack_jet`, "Ground attack jet"), `components/sentinel.json` (`heavy_bombs`,
`attack_gun`, `aam_ir_light`), `warheads.json` (`heavy_cannon`), `air_classes.json` (`attacker`, appended at the end);
code: `Sim/Systems/AttackPlanSystem.cs` (the pass plan: fire modes, the queue of aims, lines; generic for any aircraft
with free-fall bombs, so the B-52 type in H4 reuses it), hooks in `AirStrikeSystem.cs` and `CommandSystem.cs`; tests
`AttackJetTests`. The owner's values are marked (owner); everything else is a default (rows 95 to 108 of "Decisions to
confirm").

- **The airframe** (class L: it needs the main air base's runway and parks only in a heavy bay; the hangar builds it in
  60 s (owner, placeholder), 3,000). Derived from the multirole's numbers: cruise 16 cells/s (x 0.8), attack 18 on full
  dry power and **no afterburner** (dash = attack; it takes off on military power), stall 6.5; corner speed 11 cells/s
  with a 4.5-cell max-g turn: 9.5 cells at cruise (the multirole 14.2), military holds 11 cells at cruise; engines 2.5
  cells/s^2 (the multirole 4), climb 18 m/s, dive 30. Low: 100 m up (200 at 1,024 cells, at most 240; the multirole
  140), runs in at 40 m, floor 8 m. Fuel 6 min at cruise (burn 60 / 100 / 130; the multirole 3.5 min), 200% on
  1,024-cell maps; transit speeds 125% there. 700 HP; sight 35 cells, air vision 30 (eyes, no radar). Guard default:
  vehicles, structures, infantry. 12 flares and 2 chaff a sortie, the jets' break. Attack runs scaled to its speed
  (align 54 cells, break 36, extend 72). Span 17.5 m, length 16.3 m; 70 cells to land, 55 to take off; service at
  1x: tank 30 s, a round 5 s, 15 HP/s (a heavy bay: 2x rearm and repair). Loss: the jets' wreck and ejection.
  Out of a heavy bay: airborne about 39 s after an order (taxi 5.5 cells/s, pushback 3; decision 142); 29.9 s since the
  faster airfield (2026-10-24), its final 17.1 s and touchdown to parked 17.8 s.
- **Toughness** (the `attacker` class; every interceptor has values for it). AA soldier (Igla): 50% at best (judged
  against 18 cells/s), a hit takes **25%** (owner: **4 hits** bring it down; `FourIglaHitsBringItDown`: 525, 350, 175,
  0 HP). Air defence vehicle: 75% at best, a hit takes 50%. The jets' missiles (`aam_radar`, `aam_ir`): 80% at best, a
  hit **kills** (it is armoured against ground fire, not against a fighter's missiles). The jets' guns: 55% a burst at
  best, 90 damage (about 8 hits). The technical's heavy MG: 50% a burst, 12 damage (58 hits: a scratch); the light IR
  missile of another attack jet 45%, a hit 50%.
- **The bombs** (owner: 8 heavy bombs, tier 6, 1.5x blast): 1,300 damage (`World.StrikeTier` 6, the 2,000 lb look),
  100% within 4.5 cells and 30% out to 12 (the 2,000 lb's 3 / 8), a large runway crater, 2,600 to a building. Released
  from the wing pylons while it passes over the target: a **level** run 40 m up at 18 cells/s; the bombs are high-drag
  (`retard` 80: 4% of their horizontal speed a tick, about 55% a second), so each lands about 11 to 13 cells ahead of
  its release point (a slick bomb from there: about 20), a steep fall just ahead of the jet. Scatter 1 cell.
- **Fire modes** (owner: `single` and `salvo4`; content `delivery.modes`, in order, the first the default):
  `single` (salvo 1: one bomb a pass at each aim point; **the default**) and `salvo4` (salvo 4: a stick of 4 along
  its path, `stickSpacing` 4 cells apart, 12 cells end to end, **centred** on the aim point). Per aircraft:
  `Unit.FireMode` (an index into `World.FireModesOf(u)`), set by `CommandKind.SetFireMode` (32, `index`); it applies
  from the next pass. Measured: salvo4 at a point, impacts 2.7 to 5.5 cells apart (the stick's 4 plus a cell of
  scatter each), centred within 0.6 cells; single mode, one bomb a pass, passes about 14 s apart.
- **Several targets** (owner, section 5: Shift-click points or units; drag a line):
  - *The queue* (`Unit.Aims`, hashed while not empty; at most 8): Shift (`Command.Queue`) on Attack (a ground unit),
    AttackGround or AttackLine given to an aircraft with bombs on a ground strike appends the aim; the first aim is
    its order. A pass bombs its first aim and every queued point or unit within the delivery's `group` (12 cells: a
    stick's length) of it, along the line from it to the farthest of them and at most `groupAside` (3 cells) off it;
    the rest wait, in queue order. After a pass, the aims that got a bomb are done and the next one is its order;
    the last one left goes on as a plain order (the E7 rules: bombs, then the gun, until the target dies or it is
    out); a lost or destroyed aim moves the queue on (`TargetDead` / `TargetLost` still reported). Any other order
    replaces the queue. Measured: points 8 cells apart bombed 9 ticks apart on one pass, a third 80 cells off on the
    next pass 19 s later.
  - *A line* (`CommandKind.AttackLine` 33: `cell` to `cell2`): one pass flown along it in the drag's direction, its
    mode's salvo spread evenly from end to end (salvo4: at 0, 1/3, 2/3 and the whole of it; one bomb: the middle).
    Its order is an attack-ground at the line's start with the line as its aim (the airfield, homing and refusal
    rules of attack-ground apply: parked without bombs it rearms first); bombed, its order is done (it breaks,
    extends and ends without a refusal). Only free-fall bombs take a line (a glide bomb jet does not). Measured: a
    36-cell line, impacts within 1 cell of 0, 12, 24 and 36 cells, from either side of it.
  - *Flying it*: a pass whose line the plan fixes (a line, or aims bombed together: `StrikeRun.Forced`) flies to an
    entry point on the line's extension, a run line plus a turn diameter behind its first drop, kept inside its fly
    zone (`StrikeRun.Entry`), turns in (the reposition turn) and aligns on the line; coming down the line already
    (within a turn radius of it, heading within about 25 degrees) it takes it at once. On its run it follows the line
    (a carrot down it) instead of steering at the aim. An unforced pass (one aim) locks its line as E13's does and lays
    its stick on it. Each run tick the release solution's impact point is checked against the next drop
    (`StrikeRun.Drops`, `DropNext`): released within half a tick's travel of it, at most one bomb a tick; skipped
    once past it by more than the release window or off the line by more than the window, the scatter and (forced)
    `groupAside`. A moving unit's drops follow it, led by the fall time. `StrikeRun.Served` and `ServedHits` tell which
    aims the pass serves and bombed.
- **Air-to-air** (owner: 2 IR missiles and its gun; it fights back but loses to the air superiority fighter): 2
  `aam_ir_light` (the jets' IR missile at 30 cells instead of 40; against jets 40 to 45% at best, a hit takes 60% of
  a jet, 50% of another attack jet: it hurts, never kills outright) and `attack_gun` (a 30 mm type: 12 bursts, 150
  damage through `heavy_cannon`: 100% to light and medium vehicles, 70% to armour, 40% to aircraft; 1.5-cell splash;
  strafing from 20 cells, a burst every 4 ticks; against aircraft its hit chances are a third to a half of the jets'
  gun's). A gun kill on it makes the pilot eject (E10). Measured (`ItFightsBackButLosesToTheAirSuperiorityFighter`,
  8 seeds, met at 130 cells and in a 40-cell merge): the fighter wins all 16, the attack jet fires back in the
  merges (4 shots, a hit in 2 of 8).
- **Elements**: two attack jets in an element given one line both bomb it (the wingman's own line through where it is;
  an offset line for wingmen is a follow-up).

### Gunship (H3)

Milestone H3 of the game repo's `docs/heavy-air-plan.md` (section 1.2, the AC-130 type; 2026-10-21 night). Content:
`archetypes/sentinel.json` (`gunship`, "Gunship"), `components/sentinel.json` (`gunship_light`, `gunship_heavy`,
`gunship_big`), `warheads.json` (`autocannon`), `air_classes.json` (`gunship`, appended at the end); code:
`Sim/Systems/GunshipSystem.cs` (the orbit, the side cannons, `World.SideFires`, `World.RadarTracks`), hooks in
`ScoutSystem.cs` (a gunship's attack orders go to it instead of the strike runs), `MissionSystem.cs` (its steering),
`FixedWingSystem.cs` (cruise and its own height on the orbit) and `AirDefenseSystem.cs` (the ceiling); tests
`GunshipTests`. The owner's values are marked (owner); everything else is a default (rows 109 to 125 of "Decisions to
confirm").

- **The airframe** (class XL: only the main air base serves it, it parks only in a heavy bay; the hangar builds it in
  90 s (placeholder), 4,000). A slow four-engine turboprop: cruise 12 cells/s (the attack jet 16), 13 on full power,
  **no afterburner**, stall 5.5; corner speed 9 cells/s with a 10-cell max-g turn: **17.8 cells at cruise** (the attack
  jet 9.5: wide turns); engines 1.5 cells/s^2, climb 10 m/s, dive 15. **High (owner):** 360 m up (plus 5 m x (ID mod
  5)) on every map, above the AA soldier's ceiling (below). **The longest endurance (owner): 45 minutes** at cruise
  (the Overseer 40, the attack jet 6; burn 60 / 100 / 120). 1,600 HP; sight 50 cells (it sees its area from the orbit),
  air vision 30 (eyes). Span 40 m, length 30 m; 90 cells to land, 80 to take off, the overhead break at 9 cells/s and
  50 m; service at 1x: tank 45 s, a round 0.5 s (121 rounds: about 30 s in a heavy bay), 25 HP/s. Its loss: a wreck
  and a 300-damage crash (no ejection: the crew is not modelled). No air-to-air weapons (owner).
- **Toughness (owner: 8 Igla hits' worth, counted against what reaches it).** The `gunship` class: every missile hit
  takes **13%** of its HP (8 hits: 1,392, 1,184, ..., 144, 0; `EightAirDefenceMissileHitsBringItDown`). The air defence
  vehicle 80% at best and the jets' missiles (`aam_radar`, `aam_ir`) 85%, the attack jet's light IR missile 70%; the AA
  soldier 60% (only when it is low: taking off or landing). Guns are a scratch: the jets' 60% a burst for 90 (about 18
  bursts), the attack jet's 30% for 60, the heavy MG's 60% for 12 below its 40 m ceiling.
- **The AA soldier's ceiling (plan 2.2):** an interceptor's `ceiling` (H3; the Igla's 320 m, the jets' highest cruise
  on big maps) is the most a target may fly over the ground under it to be engaged; the gunship's 360 m orbit is above
  it everywhere (a soldier on a hill too), and above the Igla's 280 m of slant range on flat ground anyway. The air
  defence vehicle reaches the orbit within about 126 cells of ground distance; jets climb to it (they pursue at the
  target's height). Measured: 8 AA soldiers under its circle for 2 minutes fire nothing; at 240 m (a test content) the
  ceiling alone decides (`TheCeilingKeepsTheAaSoldierOffAGunshipAboveIt`).
- **The orbit attack (owner).** The order: Attack-ground at a cell (the area's centre), or Attack at a ground unit (the
  area follows it while its side sees it, else its last known cell; destroyed, the order becomes an attack-ground
  order there and it works the area on: `TargetDead` reported). It never flies strike runs. It flies a **left-hand
  pylon turn** of `gunship.radius` (30 cells) round the centre, counter-clockwise on the map: the drones' orbit law
  (a point 30 degrees ahead) on a circle of radius / cos 30 degrees, so it settles on its radius (measured 30.2 cells,
  on the circle 100% of a settled orbit). It is **on the circle** within `onCircle` (4 cells) of the radius with the
  centre within `bearing` (30 degrees) of its left wing, and **fires only then** (owner): on its way in, or swung
  wide, it holds its fire. It orbits and fires until every round is gone (then it goes home on its own **to rearm**:
  refuelled, rearmed to 100/20/1 (about 30 s in a heavy bay) and repaired, then held in its bay until its next order;
  `NoAmmo` reported; decision 141, the 2026-10-21 fix: it was a recall that lost its hold and pushed back out with no
  rounds; `HeavyBayTests`),
  another order, or bingo fuel (the order ends with `Bingo`, home to refuel, the E2 rule). Its state is
  `Unit.Gunship` (`GunshipOrbit`: centre, ordered target, on the circle, joined, rounds fired, each cannon's reload;
  hashed while present); `World.AirStatusOf` says `GunOrbit`. Gunships never fly element slots on the orbit: several of
  an owner's on one area (the same centre) space themselves round the circle, each slowing (to its corner speed at
  most) while the nearest ahead of it is closer than 360 / n degrees (measured, two given one order side by side: 46 to
  58 cells apart after a minute, the circle's diameter 60, both firing all 121 rounds).
- **Targets (fog-honest).** Each cannon fires at the ordered unit when its armour class is in the cannon's
  `sideFire.priority`, else at the best enemy its side sees (its own sight from the orbit included) within
  `gunship.area` (15 cells) of the centre: the earliest listed armour class, then the nearest to the centre (the very
  big round: the most max HP), ties to the lower ID. With none, a cannon with `areaFire` fires at a seeded point of the
  area on an attack-ground order (not on an attack order); the others look again twice a second.
- **The side cannons (owner: 100 light, 20 heavy, 1 very big; guns on the left side, inaccurate).** Each fires on its
  own reload; a round flies straight from the aircraft at the cannon's speed (`Projectile.Bomb` with `Shell`) to its aim
  point (a moving target led by the flight time; an aimed round scattered up to `sideFire.scatter` per axis, seeded)
  and goes off there like a bomb: the direct hit on what stands there, the splash, the runway crater, a
  `World.BombImpacts` entry (`Shell`, the cannon's look `tier`).
  - *Light* (`gunship_light`, a 40 mm type): 100 rounds, one every 0.4 s, 45 damage through `autocannon` (soldiers
    100%, light and medium vehicles 80%, armour 15%, buildings 10%), a 1.5-cell splash, 20 cells/s, scatter 3 cells;
    at soldiers, light and medium vehicles and parked aircraft; area fire.
  - *Heavy* (`gunship_heavy`, a 105 mm type): 20 rounds, one every 2.5 s, **half the multirole's 500 lb bomb** (350
    through `bomb`, its rings 2 / 5 cells and small runway crater) with **its look** (tier 4); 15 cells/s, scatter 2.5
    cells; at vehicles first, then structures, parked aircraft, soldiers; area fire.
  - *Very big* (`gunship_big`): 1 round, the attack jet's heavy bomb (1,300, 4.5 / 12 cells, a large crater, tier 6),
    10 cells/s, scatter 2 cells; only at a structure or an armoured vehicle (the biggest seen in the area, or the
    ordered unit when it is one): never area fire, so it may come home unfired.
  Measured (`ItFiresOnlyOnItsCircleNeverOnTheWayIn`: an area with a technical, a truck, three riflemen and a depot,
  160 cells off: on its circle 12.4 s after the order): all 121 rounds within 80 s of the order, every one on the circle;
  aimed rounds land up to their scatter off (`ItsRoundsLandScatteredWithinTheirBound`: mean 2 cells for the light cannon).
- **Flares (owner, 2026-10-21):** 12 salvos a sortie, fired in the "angel wing" pattern from its sides (a client look):
  the existing flare rule (one salvo a missile within 2 s of it) with a missile's kill chance x 35% (a jet's flares
  55%); no chaff, no break (it holds its orbit). Refilled on a rearm.
- **The H1 follow-ups.** The Watcher and the Overseer have a `buildTime` (60 and 45 s, their stations' replacement
  times) for the main air base's hangar (they showed 0 s); their stations launch, recover and replace them as before.
  `World.RadarTracks(radar)`: the air units (flying aircraft, the FPV drone in the air) of other players a radar
  structure itself detects now (its `airVision` slant from its top, signature-scaled), in ID order; empty when it is
  off: what its dish follows.
- **For renderers:** `World.SideFires` (`SideFireShot`: the round's ID, the gunship, the weapon index 0 light / 1 heavy
  / 2 very big in its archetype's order, the target or 0, the gunship's position and height, the aim, the impact tick,
  the look tier, area fire), the round in flight (`World.Projectiles`, `Bomb.Shell`), its impact (`BombImpacts` with
  `Shell`), the orbit (`Unit.Gunship.Centre`, `OnCircle`; the radius from `Archetype.Gunship.RadiusRaw`), flares
  (`World.Countermeasures`), `World.RadarTracks`.

### Strategic bomber (H4)

Milestone H4 of the game repo's `docs/heavy-air-plan.md` (section 1.3, the B-52 type; 2026-10-21 night). Content:
`archetypes/sentinel.json` (`strategic_bomber`, "Strategic bomber"), `components/sentinel.json` (`strategic_bombs`),
`air_classes.json` (`bomber`, appended at the end); code: H2's pass plan (`Sim/Systems/AttackPlanSystem.cs`) with two
generic additions, a fire mode's `spread` (a carpet) and a delivery's `overfly` (it flies on over its drops before it
breaks), hooked in `AirStrikeSystem.cs`; tests `StrategicBomberTests`. The owner's values are marked (owner); everything
else is a default (rows 126 to 140 of "Decisions to confirm").

- **The airframe** (class XL: only the main air base serves it, it parks only in a heavy bay; the hangar builds it in
  120 s (owner, placeholder), 5,000). An eight-engine jet bomber at **medium speed (owner)**: cruise 14 cells/s, 15 on
  full power (the gunship 12 / 13, the attack jet 16 / 18), **no afterburner**, stall 6; corner speed 10 cells/s with a
  12-cell max-g turn: **23.5 cells at cruise** (the gunship 17.8, the attack jet 9.5: wide turns); engines 1.5
  cells/s^2, climb 10 m/s, dive 15. **High:** 400 m up (plus 5 m x (ID mod 5)) on every map, and its bomb runs too
  (ingress and release height 400 m), above the AA soldier's 320 m ceiling. **Long range (owner): 30 minutes** of fuel at
  cruise (the jets 3.5 to 6, the attack jet 6: five times its tank; the gunship keeps the owner's "longest of all" at
  45). 2,000 HP; sight 40 cells, air vision 30 (eyes). It may fly 65 cells past the map's edge (twice its turn at cruise
  plus a second: now the widest fly zone, so `World.FlyZoneMargin` is 65 cells). Size 0.82 of the real type: span 46 m,
  length 40 m (the heavy bay's door is 48 m wide). Service at 1x: tank 60 s, a bomb 3 s (20: 30 s in a heavy bay, 2x),
  25 HP/s; 100 cells to land, 90 to take off, the overhead break at 10 cells/s and 50 m. Its loss: a wreck and a
  400-damage crash over 6 cells (no ejection: the crew is not modelled). Guard default: vehicles and structures.
  Out of a heavy bay: airborne about 43 s after an order (taxi 5.5 cells/s, pushback 3; decision 142); 32.1 s since the
  faster airfield (2026-10-24), its final 27.9 s and touchdown to parked 18.4 s.
- **Toughness (owner: 6 missile hits).** The `bomber` class: every missile hit takes **17%** of its HP (1,660, 1,320,
  980, 640, 300, 0: `SixAirDefenceMissileHitsBringItDown`). The air defence vehicle 85% at best (a big radar target),
  the jets' missiles (`aam_radar`, `aam_ir`) 85%, the attack jet's light IR missile 70%, the AA soldier 60% (only when it
  is low: taking off or landing); `speedRefs` 15 cells/s. Guns scratch it: the jets' 60% a burst for 90 (about 23
  bursts), the attack jet's 30%, the heavy MG's 60% for 12 below its 40 m ceiling. At 400 m the air defence vehicle
  reaches it within about 92 cells of ground distance (220 cells of slant).
- **The AA soldier cannot reach it.** 400 m is above the Igla's 320 m ceiling everywhere and above its 280 m of slant on
  level ground. Measured (`AaSoldiersCannotReachIt`): ten AA soldiers along its run line and round the target, a full
  carpet run, the break and the extend: not one launch, its lowest point 400 m.
- **The bombs (owner: 20 very heavy bombs, tier 7):** `strategic_bombs`, 2,000 damage (`World.StrikeTier` 7; 4,000 to a
  building), 100% within 5 cells and 30% out to 13, a large runway crater; slick, dropped **level from 400 m**: a fall
  of about 3.4 s that lands about **50 cells ahead of the release point**, so each bomb is released well before its
  aim. Scatter 1.5 cells. **Overfly** (`delivery.overfly`): after the pass's last drop it flies on down its run line (the
  line carrot, a forced pass's steering, now also for an unforced overfly pass) until it has passed over the last drop,
  then breaks; the slick bombs keep its speed, so they land under it (measured: the break began the tick the last bomb
  went off, 1 cell past it).
- **Fire modes (owner):** `carpet20` (salvo 20, `spread`: **the default**, the owner's "all on target") and `salvo4`
  (salvo 4: a stick of 4 at each aim point, the attack jet's rule). `stickSpacing` 2.5 cells: a carpet is 47.5 cells
  end to end, a salvo4 stick 7.5. A **spread** mode lays one stick for the whole pass instead of one at each aim: on a
  lone aim, centred on it; on a dragged line, evenly from end to end; on aims bombed together, evenly from the first
  along the run to the last; never closer than `stickSpacing` (a shorter line or group is stretched about its middle:
  at most one bomb leaves a tick, 0.75 cells of travel); each drop serves the aim nearest it. Measured: carpet20 on a
  point, 20 impacts 0.8 to 5.2 cells apart (mean 2.9), 48.4 cells end to end, centred within 0.5 cells, all in one
  pass of 3.2 s; salvo4, two passes 33 s apart, sticks of 4 centred within 0.7 cells; an 80-cell line, 20 impacts
  within 2.5 cells of their even places; a 20-cell line, the 47.5-cell carpet centred on it.
- **Several targets** (owner, as the attack jet's): Shift queues on Attack, AttackGround and AttackLine; aims within
  `group` (48 cells: the carpet's length) of a pass's first, along its line and at most 3 cells off it, share the pass.
  With salvo4, a stick of 4 at each (measured: two points 20 cells apart, 8 bombs in one pass, then a third point 120
  cells off on the next); with carpet20 one carpet spread across them (two points 30 cells apart: one 47.5-cell carpet
  centred between them). Points beyond the group with carpet20: the first pass drops all 20, the later aims get
  nothing (`NoAmmo`; use salvo4 for several far targets).
- **Every bomb spent: home to rearm** (decision 143, the 2026-10-21 polish; it idled in the air after a dragged-line
  carpet). A planned pass (a line, a queue, a fire mode's stick or carpet) that ends the strike with every weapon stock
  empty sends the aircraft home on its own to rearm, the gunship's rule (decision 141): `HomeReason.Rearm`, the full
  service in its heavy bay (20 bombs and 30 flare salvos), then held parked until its next order. It applies on a line
  (its pass `Final`), on a point and on a queue whose far aims get nothing (`StrategicBomberTests.EveryBombSpent...`).
  An aircraft with anything left keeps the owner's rule (E2: it orbits on): the attack jet after its last bombs on a
  line still has its gun and missiles, so it orbits; with nothing else left it goes home the same way
  (`AttackJetTests.ItsLastBombsOnALine...`). A weapon without a stock is never spent; only runway aircraft.
- **Its bomb run** (archetype `attackRun`): a long, steady run: the align leg 80 cells (5.3 s; with a 50-cell fall a
  degree of heading error moves an impact about a cell aside, so its wide turn must settle on the line first), IP 15
  cells, the break at least 60 degrees and 30 cells, the extend at least 60 cells and until it has room to turn in.
  A forced pass's entry point is a run line plus a turn diameter behind its first drop: about 200 cells out.
- **No air-to-air weapons (owner).** It carries only its bombs: it never fires at an aircraft on any order or task
  (`ItNeverFiresAtAircraft`: an attack order at a jet and Guard against aircraft, 60 s beside an enemy jet: nothing).
- **Flares (owner: many):** 30 salvos a sortie, two a missile (each leaves 55% of its kill chance, the jets' flare: about
  30% left after both, the jets' break-and-flares cut) and **no break** (a heavy holds its run, as the gunship holds its
  orbit); no chaff. Refilled on a rearm. Measured (`ItSpendsItsThirtyFlaresTwoAMissile`): nine attack jets, 15 missiles
  decoyed twice each, the stock 29 down to 0.
- **The AA ceiling's side effect (H3's D-112, reviewed):** kept. The Igla's 320 m ceiling is under both heavies' heights
  and over every jet's cruise except the air superiority fighter's on maps of 1,024 cells and more (336 m; 384 m at
  most). There the Igla's 280 m of slant range already keeps it out of reach over level ground: the ceiling only takes
  away a shot from a soldier standing at least 56 m (at 1,024 cells; 104 m at the 384 m cap) above the ground under the
  fighter. On a strike every jet runs in far below the ceiling (40 to 80 m). No jet behaviour changes
  (`TheAaCeilingCutsOnlyAFighterCruisingOnABigMapAndOnlyFromAHill`).
- **For renderers:** the bombs are ordinary free-fall bombs: `World.BombReleases` (one per bomb, its weapon index 0, the
  release point and height, the predicted impact and tick; a carpet is 20 releases about 3 ticks apart), the bomb in
  flight (`World.Projectiles`, `Bomb`), `World.BombImpacts` (tier 7); the pass (`Unit.Strike`: `Planned`, `Forced`,
  `Drops`, `DropNext`, phase `Run` until it has overflown the last drop); flares (`World.Countermeasures`); fire
  modes (`World.FireModesOf`: `carpet20`, `salvo4`).

### Supersonic bomber

A B-1 type (the owner's brief and answers of 2026-10-22, the game repo's b1 spec; the strategic bomber is its template:
its rules hold unless said here). Content: `archetypes/sentinel.json` (`supersonic_bomber`, "Supersonic bomber"),
`components/sentinel.json` (`supersonic_bombs`); code: a fire mode's rows across the path in H2's pass plan
(`Sim/Systems/AttackPlanSystem.cs`: `PlacesOf`, `ExpandRows`; the release in `AirStrikeSystem.cs`: `ReleaseBomb`'s
`side`, `SideVel`); tests `SupersonicBomberTests` (15) and the heavy bay's two theories. The owner's values are marked
(owner); everything else is a default (rows 146 to 157 of "Decisions to confirm").

- **The airframe** (class XL: only the main air base serves it, it parks only in a heavy bay; the hangar builds it in
  100 s, 4,500; placeholders). A swing-wing four-engine jet bomber, **faster than the strategic bomber (owner)**: cruise
  18 cells/s (measured 18.1), **22 on its bomb run** (measured released at 21.5 to 21.9 from a straight approach; the
  strategic bomber's 15), dash 24 on its afterburners (takeoff, a break's boost), stall 7; engines 4 cells/s^2 (measured:
  from a quick turn's 12 cells/s to 17 in 3 s and 20.4 in 7 s). Corner speed 12 cells/s with a 7-cell max-g turn: **15.7 cells at cruise**
  (owner: tighter than the strategic bomber's 23.5, wider than the jets' 9.5 to 14.2), 23.5 at its run speed. **The same
  height (owner): 400 m** (plus 5 m x (ID mod 5)) on every map and on its runs, above the AA soldier's 320 m ceiling.
  25 minutes of fuel at cruise (burn 100 / 140 military / 250 afterburner). 1,700 HP; sight 40 cells, air vision 30
  (eyes). Fly zone 50 cells (twice its turn at cruise plus a second; the strategic bomber's 65 stays the widest). Size
  0.82 of the real type: span 34 m spread (the swing wings are the client's: spread slow, swept fast), length 36.5 m, a
  little smaller than the strategic bomber (owner). Service at 1x: tank 50 s, a bomb 2.5 s (24: 30 s in a heavy bay),
  25 HP/s; 100 cells to land, 90 to take off, the overhead break at 10 cells/s and 50 m, the heavies' taxi. Measured
  (`HeavyBayTests`): airborne 43.0 s after an order from a heavy bay; final 29.6 s, touchdown to parked 27.6 s (since the
  faster airfield, 2026-10-24: 32.5, 19.7 and 18.3 s). Its loss:
  a wreck and a 350-damage crash (no ejection). Guard default: vehicles and structures.
- **Toughness:** the strategic bomber's `bomber` air class: every missile hit takes 17% (6 hits: 1,411, 1,122, 833,
  544, 255, 0: `SixAirDefenceMissileHitsBringItDown`); guns scratch it. **The AA soldier cannot reach it** (owner): only
  the air defence vehicle (within about 92 cells of ground distance at 400 m), the missile defence and the jets.
  Measured (`AaSoldiersCannotReachIt`): ten AA soldiers along its block24 run, not one launch, its lowest point 400 m.
- **The bombs (owner: 24, a bit lighter than the strategic bomber's):** `supersonic_bombs`, 1,400 damage (2,800 to a
  building; `World.StrikeTier` 6: a crater and blast a bit smaller than tier 7's), 100% within 4 cells and 30% out to 10
  (the strategic bomb's 5 / 13), a large runway crater. Slick, level from 400 m: a fall of about 3.4 s that lands about
  75 cells ahead of the release point at 22 cells/s. Scatter 1 cell per axis (the strategic bomb's 1.5). **Overfly**:
  it flies on over its drops before it breaks.
- **Rows across the path (owner: "obliterate an area with 4 bombs, not a straight line"; generic).** A fire mode's
  `across` (content `delivery.modes[].across`, 1 to 8, the salvo whole rows of it) and `acrossSpacing` (tenths of a
  cell; 0: the delivery's `stickSpacing`): the plan lays salvo / across **places** exactly where a stick's bombs would go
  (at each aim, or spread along a carpet, by the H2 / H4 rules), then each place becomes a row of `across` bombs side by
  side across the run, `acrossSpacing` apart, centred on it (`ExpandRows`: offset (2k - (across - 1)) x spacing / 2 along
  the run's left-hand normal, integer). The row's drops keep the place on the run line (`StrikeRun.Drops`: the release
  solution aims there, the steering and the overfly use it); their offsets are `StrikeRun.DropSide`, `StrikeRun.Across`
  says how many make a row. When the place comes up, the **whole row leaves in that tick** (the one-bomb-a-tick rule holds
  between rows); each bomb gets the velocity across that carries it its offset over its fall (offset x 20 / fall ticks;
  a retarded bomb's worked out by stepping its drag) and its landing predicted again along its own fall, so
  `World.BombReleases`' `Predicted` is where it lands (measured: within 0.1 cells, on the B-1 and on an attack jet given a
  row mode in test content). Hashed only when a pass has rows: every other bomb hashes as before.
- **Fire modes (owner):**
  - `row4` (**the default**): a row of 4 across the path at each aim, 3 cells apart (9 cells wide), centred on it; 24
    bombs = 6 rows a sortie. Measured: one row a pass, all 4 in one tick, 9.1 and 9.9 cells wide, centred within 0.4 and
    0.7 cells, every bomb within 1.3 cells of the aim along the path; the next pass 20.6 s later.
  - `block24`: all 24 in one pass, 6 rows of 4 stacked along the path 3 cells apart (`stickSpacing`), a filled block of
    about 9 x 15 cells centred on the aim (the H4 spread: `spread: true`). Measured on a point: 10.7 x 17.1 cells between
    the outermost impacts (the scatter adds to the 9 x 15), rows 1.6 to 3.8 cells apart, centred within 0.1 cells,
    released over 14 ticks (0.7 s); **no gaps**: every point of the 9 x 15 block is within **2.5 cells** of an impact
    (the kill zone is 4).
  - On a **dragged line** (`AttackLine`): block24's 6 rows evenly from end to end (at least 3 cells apart: a shorter
    line is stretched about its middle), each a row of 4 across the line (measured, a 60-cell line: rows within 1.2
    cells of 0, 12, ..., 60); row4: one row at the line's middle (its one place, as `single`).
  - **Shift-queued aims** within `group` (30 cells) of a pass's first, along its line and at most 3 cells off it, share
    the pass: row4 lays a row at each (measured: two points 15 cells apart, 8 bombs in one pass, rows centred within 1.5
    cells; a third 120 cells off on the next pass); block24 spreads its 6 rows from the first to the last.
- **Every bomb spent: home to rearm** (decision 143): measured after a block24 pass and after one row4 pass over six
  queued points 5 cells apart (all 24 in one pass): homing (`HomeReason.Rearm`), parked in a heavy bay, 24 bombs and 30
  flares back, held there.
- **No air-to-air weapons; flares:** as the strategic bomber's: it never fires at aircraft (`ItNeverFiresAtAircraft`);
  30 flare salvos, two a missile, no break, no chaff (`ItSpendsItsThirtyFlaresTwoAMissile`).
- **The other bombers are unchanged:** their modes have `across` 1 (no rows, no `DropSide`, one bomb a tick:
  `TheOtherBombersLayNoRows`); the strategic bomber's and the attack jet's tests pass as before.
- **For renderers:** the pass's rows: `Unit.Strike` (`Across`, `Drops` (the rows' places, each repeated `Across` times),
  `DropSide`, `DropPoint(i)` = place + side: where drop i goes, `DropNext`); each bomb: `World.BombReleases` (a row is
  `Across` releases in the same tick, each with its own `Predicted` impact and `ImpactTick`), `World.Projectiles`
  (`Bomb`), `World.BombImpacts` (tier 6); fire modes `World.FireModesOf` (`row4`, `block24`; `FireModeDef.Across`,
  `Rows`, `AcrossSpacingRaw`); flares `World.Countermeasures`. The swing wings and the bomb bay doors are the client's.

### Stealth bomber

A B-2 type (the owner's brief and answers of 2026-10-22, the game repo's `b2-spec.md`; the strategic and supersonic bombers
are its template: their rules hold unless said here). Content: `archetypes/sentinel.json` (`stealth_bomber`, "Stealth bomber"),
`components/sentinel.json` (`stealth_bombs`, `bunker_busters`), `air_classes.json` (`stealth`, appended), `warheads.json`
(`bunker_buster`), `exclusive_groups.json` (`heavy_bomber`); code: precision bombs and several bomb weapons in the pass plan
(`Sim/Systems/AttackPlanSystem.cs`: `PlacePrecision`, `SetBombWeapon`), the penetrator (`Sim/Systems/PenetratorSystem.cs`),
exclusive groups (`Sim/Systems/ExclusiveSystem.cs`); tests `StealthBomberTests` (21) and the heavy bay's two theories. The
owner's values are marked (owner); everything else is a default (rows 194 to 211 of "Decisions to confirm").

- **The airframe** (class XL: only the main air base serves it, it parks only in a heavy bay; the hangar builds it in 130 s,
  6,000; placeholders). A flying wing: **very high (owner): 700 m** (plus 5 m x (ID mod 5)) on every map and on its runs (the
  other heavies 400); cruise 15 cells/s (measured 15.0), 16 on its run, no afterburner, stall 6.5; corner speed 10.5 cells/s
  with a 10-cell max-g turn: 20.4 cells at cruise; climb 12 m/s; fly zone 60 cells. 30 minutes of fuel. 1,600 HP; sight 40
  cells, air vision 30. Size 0.82 of the real type: span 43 m (21.5 cells), length 17 m: through the heavy bay's 48 m door.
  Measured (`HeavyBayTests`): airborne 42.5 s after an order from a heavy bay; final 36.1 s, touchdown to parked 27.7 s
  (since the faster airfield, 2026-10-24: 32.1, 25.1 and 18.4 s).
  Service at 1x: tank 60 s, a round 3.25 s (16 + 2: 29.2 s in a heavy bay, measured), 25 HP/s. Its loss: a wreck and a
  350-damage crash (no ejection). Guard default: vehicles and structures.
- **Hard to see (owner): a quarter of the range, three times the lock.** Signature `detect` 25, `lock` 300, `kill` 70. Measured
  (`EverySensorDetectsItAtAQuarterOfItsRange`, a sensor and the bomber at the same height): the mobile radar sees it within
  100 cells of slant (a normal aircraft 400), the air defence vehicle within 65 (260), the fighter within 40 (160). **At 700 m
  (350 cells up) that is never**: no sensor in the game detects it in flight, and no ground weapon reaches 700 m anyway (the
  air defence vehicle's missile 220 cells of slant, the missile defence's 280, the AA soldier's ceiling 320 m). Measured
  (`At700MNothingSeesOrReachesIt`): a mobile radar, three air defence vehicles and five AA soldiers under its run: never
  tracked, not one launch, all 16 bombs on target; the strategic bomber at 400 m over the same radar: tracked at once. It is
  seen and shot only low: climbing out and on its approach near its base (row 195). The lock: the air defence vehicle's 1 s
  becomes 3 s (60 ticks; radar-cued 42; `ItsLockTakesThreeTimesAsLong`).
- **Fragile once seen (3 missile hits).** The new `stealth` air class: every interceptor that engages the `bomber` class engages
  it with the same best chance and speed reference (7 interceptors, the SA-6's `kub_missiles` included), a hit takes twice the
  bomber's share: **34%** (the SA-6's 44%, the missile defence's 100%: it destroys outright anyway), guns as the bomber's. Measured (`ThreeAirDefenceMissileHitsBringItDown`, test content flying it at 80 m): 1,056,
  512, 0.
- **Two weapons the player picks between (owner).** `World.BombWeaponsOf(u)` lists an aircraft's bomb weapons (free-fall
  bombs with fire modes): the precision bombs (0, the default) and the bunker busters (1). `CommandKind.SetBombWeapon` (37,
  `Index`) selects one from its next pass (`Unit.BombWeapon`); `CommandKind.SetFireMode` sets the selected weapon's mode and
  each weapon keeps its own (`Unit.FireModeOf(slot)`; `World.FireModesOf(u)` the selected one's, `FireModesOf(u, slot)` any).
  Only the selected weapon is dropped, on orders and on Guard; with it spent the strike ends (the other is never used on its
  own). Every other aircraft has one bomb weapon at most: nothing changes for them.
- **Precision bombs (owner: 16):** `stealth_bombs`, 1,600 damage (3,200 to a building through the bomb warhead), 100% within
  2 cells and 30% out to 4, a large runway crater, **tier 7** for renderers (`delivery.tier`: a very big explosion, owner).
  Released level from 700 m: a fall of 4.5 s about 67 cells ahead. **Precision** (`delivery.precision`, generic): no scatter;
  each bomb is steered onto its own drop point: the release solution's miss along and across the run and the drop's offset
  aside of the run line go into its release velocity (the B-1 row push, both axes), and its landing is predicted again; every
  drop due in a tick leaves in it. Measured: every impact within 0.01 cells of its planned point.
  - `all16` (the default, owner: all 16 at once): on a lone point a **4 x 4 grid 2 cells apart** (6 x 6 cells), centred on it
    (measured: rows and columns at -3, -1, 1, 3 cells, centre 0.00 off, released over 7 ticks).
  - `group4` (owner: in 4s): on a lone point a **2 x 2** (2 cells apart) a pass (measured: 4 passes of 4).
  - **Shift-queued aims (owner: up to 16):** `delivery.aims` 16 (the jets' 8). A pass serves its first aim and the queued
    ones within `group` 40 cells along its line and `groupAside` **12 cells** off it, **at most one aim a bomb** (all16: 16
    aims; group4: 4), in queue order; the pass's bombs are shared out among them (salvo / aims each, the first ones one
    more), each aim's share a grid of the mode's `across` columns centred on it. Measured: 16 aims over 30 x 12 cells, one
    bomb each in one pass, every one within 0.01 cells; two aims, 8 each (4 x 2); group4 over 8 aims, two passes of 4, one
    bomb on each. On a dragged line (`AttackLine`) the salvo is spread evenly from end to end.
- **Bunker busters (owner: 2):** `bunker_busters`, 6,000 damage within 3 cells through the new `bunker_buster` warhead (100%
  against every armour, 200% against buildings), tier 7, and the **penetrator** (`weapon.penetrator`, generic): where it goes
  off it destroys **everything on the ground within 3 cells whatever its armour** (a structure by its footprint, vehicles,
  soldiers, parked aircraft, and whoever is inside them: a garrison, a crew, riders die in them), **kills every unit
  underground in a tunnel node it hits** (an entrance whose footprint is within the 3 cells: everyone waiting at it, and
  anyone on a passage whose place is within the 3 cells; the only weapon that can) and **collapses that entrance into
  rubble** (closed: nobody goes in or out; still a junction). Flying aircraft and the airfields' pavement are untouched. It
  falls slower and steeper (gravity 55, drag 10%/s: 5 s from 700 m). Precision-steered, no scatter. Fire modes (owner):
  `both` (the default: on a lone point the two side by side across its path 3 cells apart, in one tick; one each on two
  queued aims) and `single` (one a pass). Measured: both at -1.49 / +1.49 cells across, single on the point within 0.01; a
  100,000-HP armoured carrier with a rider, a garrisoned house and a soldier within 3 cells destroyed, a carrier 6 cells off
  untouched; a bunker buster 0.2 cells from an entrance: all 8 units below it killed, the entrance rubble, another node's units
  alive. The precision bombs on an entrance: it falls (rubble, then gone), nobody underground dies (the survival rules move
  them to a linked entrance).
- **The deep crater (owner: a special crater):** `CraterKind.Deep` (appended), on `BombImpact.Crater` for every bunker buster
  wherever it lands, and `World.Craters` (`GroundCrater`: the bomb, centre, radius 1.5 cells, `Since`, `Until`; hashed) for 5
  minutes (`craterTicks` 6,000). Visual only: it does not block movement. On a runway it also digs the large runway crater.
- **Home to rearm** (decision 143): only when **both** weapons are spent (measured: 16 precision bombs gone, it orbits on with
  its 2 bunker busters; both spent, home, 16 + 2 and 8 flares back in 29.2 s).
- **No air-to-air weapons; few flares (it relies on stealth):** 8 salvos, one a missile, no break, no chaff.
- **For renderers:** the weapon toggle and modes (`World.BombWeaponsOf`, `Unit.BombWeapon`, `World.FireModesOf(u, slot)`,
  `Unit.FireModeOf`); the pass (`Unit.Strike`: `Drops` (each drop's place on the run line), `DropSide` (its offset aside),
  `DropPoint(i)` (where it lands), `Across` 1); each bomb `World.BombReleases` (its `Predicted` impact, exact), the bomb in
  flight (`World.Projectiles`, `Bomb`), `World.BombImpacts` (tier 7, `Crater` Deep for a bunker buster); the tunnel kill events
  `World.TunnelKills` (`TunnelKill`: bomb, unit, entrance, owner; each unit killed underground) and `World.TunnelCollapses`
  (`TunnelCollapse`: bomb, entrance, owner, position); the deep craters `World.Craters`. The bay doors, the flat exhausts and
  the explosion's look are the client's.

### The heavy bombers' exclusive group ("choose 1 of 3")

The owner's rule (2026-10-22): a player may have **at most one heavy bomber** at a time (the strategic, supersonic and stealth
bombers), and the first one built **locks the type for the match** (a lost one can be rebuilt as the same type). Content: a new
root list `exclusiveGroups` (`content/mvp/exclusive_groups.json`: `id`, `max`, `lockType`) and an archetype's `exclusive` (the
group's id). The count: the player's units of the group alive (parked, taxiing, in the air) plus those queued or in a factory's
bay or door. The lock (`Player.ExclusiveLocks`, by group: an archetype index or -1; hashed): set when the player's production
first finishes one of a `lockType` group; units spawned outside production (a setup's army, the sandbox) count but lock nothing.
`Produce` refuses an order that would break the rule (silently, as its other refusals); `World.ExclusiveOf(player, archetype)`
gives the client the state (`ExclusiveState`: group, max, count, the locked archetype, `Refusal` None / AtMax / Locked,
`Allowed`) to grey out the others and show the lock. "One alive at a time, any type" is `lockType: false` (data; tested).
Measured (`OneHeavyBomberAtATimeItsTypeLockedByTheFirst`): one queued, every second order refused; built, locked to it; lost,
the same type accepted and the other two refused; the other player unaffected.

### Stealth multirole (F-35 type)

An F-35 type (the owner's brief and answers of 2026-10-24, the game repo's `f35-spec.md`): the jet that opens the door. It goes in
stealthy, finds the enemy radars just by flying through their coverage, and can come back as a heavy bomber. Content:
`archetypes/sentinel.json` (`stealth_multirole`, "Stealth multirole"), `components/sentinel.json` (`f35_aam`, `f35_bombs`), the
tag `air_radar` on the mobile radar and the air defence vehicle (`archetypes/vehicles.json`), its own line in
`factions/sentinel.json`; code: loadout modes (`Sim/Systems/LoadoutSystem.cs`, `Unit.Loadout`, `Unit.StockMax`, `Unit.Signature`,
`World.FlightOf`), radar spotting (`Sim/Systems/RadarSpotSystem.cs`, hooked into `VisionSystem.cs` and `Fog.cs`); tests
`StealthMultiroleTests` (19). The owner's values are marked (owner); everything else is a default (rows 346 to 360 of "Decisions
to confirm").

- **The airframe** (class M; **built only by the main air base's hangar (owner), no cap (owner)**: the forward airfield's facility
  excludes it by the tag `stealth_jet`, `production.excludeTags`, now read for producers into a station; it lands, parks and is
  serviced at any airfield of its side that takes class M). 55 s, 3,200, 300 HP, 3.75 minutes of fuel at cruise, 40 cells of
  sight, a 150-cell air radar (placeholders). The multirole's airframe numbers: its fixed-wing envelope, turns, height by map,
  attack runs, elements, pursuit, break, crash and ejection, overhead pattern and taxi; 10.7 m span, 15.7 m long; a round loads in
  4 s at 1x (12 rounds: about 24 s at a loading shelter). Air defence engages it as the `fighter` class; its signature does the
  rest. On Guard it engages vehicles, structures, aircraft and drones.
- **Two loadout modes** (archetype `loadouts`, generic: per mode its weapons' rounds by component, a speed percent and a signature;
  `loadoutSwitch` the ticks a change takes). It is built in the first. `Unit.Loadout` (hashed when not 0) is its mode;
  `Unit.StockMax(i)` is what each stock holds in it (what a Rearm fills to: every reader of a stock's capacity now asks the unit),
  `Unit.Signature` its signature (detection, lock and kill against it), `World.FlightOf` its flight at the mode's speed (the fly
  zone widened to its turns; a strike's runs keep the content's attack speed, D-SPD1's rule).

| | Stealth (the default) | Heavy |
| --- | --- | --- |
| Heavy bombs / radar air-to-air missiles | **2 / 10 (owner)** | **10 / 2 (owner)** |
| Cruise / attack / dash (cells/s, maps up to 512 cells) | **26 / 29.9 / 36.4: 1.3x the multirole (owner)**, the fastest jet (the fighter 21 / 24 / 30) | **20 / 23 / 28: the multirole's (owner)** |
| Turn at cruise (the same g) | 24 cells | 14.2 cells |
| Signature detect / lock / kill | **35 / 250 (owner)** / 65 | **80 / 130 (owner: to tune)** / 90 |
| Seen by (measured) | mobile radar 140 cells of slant, Osa 91, fighter 56 | 320, 208, 128 |
| Osa lock (1 s on a normal jet) | 2.5 s (50 ticks) | 1.3 s (26 ticks) |

- **The mode change: only on the ground (owner).** `CommandKind.SetLoadout` (42; `Index` the mode) starts it only when the
  aircraft is parked at a station of its side's airfield that rearms now and draws on no limited stock: a stand or a shelter of a
  main air base or a forward airfield (a shelter needs its facility standing); never in the air, holding, landing or taxiing,
  never at a forward airbase (logistics: its limited stock). **It takes 200 ticks (owner: about 10 s)** (`Unit.LoadoutTo`,
  `LoadoutLeft`, hashed while changing), holds the aircraft at its station (an order given meanwhile launches it once the change
  is done; measured: ordered 150 ticks before the end, it left 150 ticks later) and ends with **the new mode's full load of every
  weapon**: the change is the reload, no Rearm order needed (flares are the same in both modes and left to the Rearm). Ordering
  the mode it has cancels a change; its station falling cancels it too. Measured: heavy 200 ticks after the order (the order's
  tick counts), 10 bombs and 2 missiles; back to stealth, 2 and 10; at a forward airfield's stand the same; a Rearm in heavy mode
  fills 3 bombs and no missiles back to 10 and 2. Refused in flight, during pushback or taxi-out, and by every other aircraft.
  `World.LoadoutOf(u)` (`LoadoutStatus`: mode, id, switching to, ticks left, ticks a change takes, `CanSwitch` for greying the
  button) and `World.LoadoutsOf(u)`.
- **Heavy bombs (owner: the multirole's bomb, 30% more damage, a bigger blast).** `f35_bombs`: the multirole's 2,000 lb (its heavier
  preset) at 1,300 x 1.3 = **1,690** damage (3,380 to a building through the bomb warhead), 100% within **4 cells** and 30% out to
  **10** (the 2,000 lb's 3 / 8), a large runway crater, the multirole's 35-degree dive delivery. Fire modes `single` (the default,
  one a pass) and `pair` (two a pass, 4 cells apart). **Strike tier 6** for renderers (`World.StrikeTier`: 1,690 is under 1,800;
  the 2,000 lb's look, kept FPS-light). Measured: a pair dropped on a point landed 4.6 cells apart, both 1,690, tier 6.
- **Air-to-air:** 10 or 2 `f35_aam`, the air superiority fighter's radar missile (110 cells, a 1 s lock, chaff decoys it). No gun,
  no IR missile. **8 flare salvos in both modes (owner: the multirole's count)**, no chaff; the jets' break.
- **Radar spotting (owner).** Archetype `radarSpotting` (generic: `tags`, `linger`, `radius`). Each tick before vision, every
  working enemy radar (alive, built, not inside, set up: the mobile radar only while set up) with an air vision and one of the
  tags is checked in ID order against every flying aircraft with radar spotting (any airborne phase, either mode): **inside the
  radar's coverage** (the slant from its eye within its full air vision, **whatever the aircraft's signature: whether or not the
  radar detects it**) the radar is revealed to the aircraft's side (`RadarSpot`, one per radar and player). Revealed means
  spotted (`Unit.SpottedBy`: visible through the fog and targetable by every unit of the side): **the radar and only a disc of 2
  cells round it** (the ground units on it; under fog the cells within 2 cells of its cell visible in the side's grid, 13 cells in all,
  nothing farther: an air viewer's disc, no line of sight). It **stays revealed 200 ticks (owner: about 10 s) after the aircraft left the coverage**. Radars that count: the
  **mobile radar** (the SA-6 battery's radar: the launcher has no radar of its own) and the **air defence vehicle (Osa)**, by
  the tag `air_radar`; **never the AA soldier** (his air vision is his eyes) or a packed-up mobile radar. Measured: orbiting 300
  cells from a set-up mobile radar (it detects stealth mode within 140), never tracked, the radar seen by its side through the fog
  from the first tick; the last tick inside 172, revealed through tick 372, hidden at 373 and the fog closing over it; a vehicle
  1.4 cells from the radar spotted, a soldier 6 cells out not; the Osa 100 cells away revealed, an AA soldier, a packed-up radar
  and an SA-6 launcher never.
- **For renderers.** The card: `World.LoadoutOf(u)`, `World.LoadoutsOf(u)` (each `Loadout`: `Id`, `Ammo`, `SpeedPercent`,
  `Signature`), `Unit.Loadout`, `LoadoutTo`, `LoadoutLeft`, the order `CommandKind.SetLoadout`; the bombs' fire modes
  (`World.FireModesOf`, `CommandKind.SetFireMode`). The revealed radars: `World.RadarSpots` (sorted by radar then player; each
  `RadarSpot`: `Radar`, `Player`, `Spotter` the aircraft, `Since`, `Until` the first tick it is hidden again, `Inside` whether an
  aircraft of the side is in its coverage now, `Radius` the disc) and `World.RadarSpotOf(radar, player)`: a small marker on the
  radar while revealed. `World.FlyZoneMargin` (the widest in the content, for the ground and camera band) is now its stealth
  mode's, about 74 cells (the strategic bomber's 65 before). The bay doors, pylons (the heavy mode's 8 external bombs shown) and
  the look are the client's.

### Logistics: the airlifter, the field ammo point, the supply stash, the forward airbase

The Sentinel's logistics (the owner's brief and answers of 2026-10-22, the game repo's logistics spec; branch `logistics`). The
owner's answers: **no capturing yet**; the forward airbase **cannot produce**, it only hosts aircraft and holds supply; **the main
air base has unlimited supply** and loading there costs nothing (no payment yet). Everything else is a default (rows 212 to 236
of "Decisions to confirm"). Content: `archetypes/logistics.json` (`airlifter`, `facp`, `supply_stash`, `forward_airbase`,
`forward_airbase_hut`, `forward_airbase_apron`, `forward_airbase_stand`), `components/logistics.json` (`cargo_pallets`,
`facp_supply`, `stash_supply`, `forward_airbase_stock`, `airbase_supply`), `airfields.json` (type `forward_airbase`),
`air_classes.json` (`airlifter`, appended); code: `Sim/Systems/AirdropSystem.cs` (the airlifter's orders, the pallets' landing),
`LogisticsSystem.cs` (queries, events), `SupplySystem.cs` (stores, the ammo point, truck loads), `AirfieldServiceSystem.cs` and
`ServiceSystem.cs` (the stock's draws); tests `LogisticsTests` (20 tests, 21 cases) and the heavy bay's landing theory. Supply is the depots'
unit (a depot holds 1,000; a cargo truck carries 400).

- **The airlifter** (`airlifter`, a C-5 type; class XL, built by the main air base's hangar in 90 s for 3,000, parked in a heavy
  bay; placeholders). Size about the B-1's (owner): 0.55 of the real type, span 37 m, length 40 m (through the bay's 48 m door).
  Unarmed; cruise **13 cells/s, 300 m up** (inside the AA soldier's 320 m ceiling); 30 min of fuel; turn at cruise 20.8 cells;
  fly zone 65 cells. 1,800 HP; the new `airlifter` air class: every interceptor engages it with the bomber's numbers and a hit
  takes **25%** (4 hits: measured 1,350, 900, 450, 0; the SA-6 33%, the missile defence 50%); 30 flare salvos, two a missile, no
  break. Its landing is the heavies' (overhead break at 9 cells/s, 90 cells to land, 80 to take off): measured at the main base,
  final 40.6 s, touchdown to parked 27.8 s (`HeavyBayTests`; since the faster airfield, 2026-10-24: 29.4 and 18.4 s). No mission
  block: it never picks targets.
- **Its cargo** (`cargo_pallets`, generic: a weapon with an `airdrop` block): **4 containers of 2,500** (10,000 a sortie;
  `World.CargoAboard`). Pallets load only at an airfield whose supply is unlimited (a main air base: a station's rearm, 15 s a
  container at 1x, **measured 30.0 s for 4 in a heavy bay**); a field with a limited stock never loads them (its `Serviced` leaves
  them out). Every container gone, it goes home on its own to load (decision 143's rule), preferring such a field (its home
  choice), and is held there loaded until its next order.
- **Airdrop** (`CommandKind.Airdrop` 38, the target a finished drop zone of the player's: `World.DropZoneOf`): an attack-ground
  pass on the zone's centre flown by the attack-run state machine (`Unit.AirdropAt` marks it; only such an order drops pallets, an
  ordinary attack order never picks them and an attack-ground order on the airlifter is refused). It descends early to the
  pallets' **150 m** release height (a low drop starts down 1.5 times its sink distance before the run) and releases by the exact
  release solution; fire modes (`SetFireMode`): **`all4`** (the default: a stick of 4, 2 cells apart, in one pass; measured
  released over 8 ticks at 150 m, about 26 cells before the zone) and **`single`** (one a pass: measured passes 31 s apart). A
  pallet falls under its parachute (gravity 4.7 m/s2, 5% of its horizontal speed lost a tick: about 8 s) with up to 2.5 cells of
  seeded scatter a side; nothing it falls past stops it, it never explodes. **Inside a drop zone** of its side (the first store by
  ID whose circle holds the point) its supply goes into the store's stock up to the cap, the rest lost; **outside every zone** it is
  lost (it burns or lies as debris; no capture yet). Measured: 12 sorties of all4 on a stash, 48 of 48 inside its 8-cell zone, the
  farthest 3.9 cells from the centre; one a pass on an empty ammo point: 2,500, 2,500, 1,000 (1,500 lost), 0 (2,500 lost).
  Events: `World.LogisticsEvents` (`PalletLanded`, `PalletLost`, with the pallet's projectile ID; the fall is in
  `World.Projectiles` and `World.BombReleases`, never in `BombImpacts`).
- **Land and unload** (`CommandKind.Deliver` 39, the target any piece of an airfield of the player's with a limited stock: the
  forward airbase): it goes there (`HomeReason.Deliver`; from the ground elsewhere it takes off first: `AirOps.RecallTo`), lands,
  taxis to a stand and unloads **one container every 7.5 s** (`airdrop.unloadTicks`; `AirOps.UnloadLeft`; `Unloaded` events) while
  a whole one fits (the rest stays aboard), takes on fuel (paid from the stock), takes off and flies home to load at the nearest
  airfield of its side with unlimited supply, held there loaded. Measured: put down in flight about 160 cells from the field, parked there at 71 s, 4 unloads 7.5 s apart
  (30 s), departed at once, home and loaded at 255 s; the stock 8,000 + 10,000 - 5 for its fuel.
- **Losses:** shot down with containers aboard, their cargo is lost (`CargoLost`, the supply aboard).
- **Vehicles** (2026-10-24, "Light tank" above): parked at the main air base it can take 2 Booker light tanks instead of its
  containers (never both) and Deliver them to a forward airbase, where they drive off.
- **The field ammo point** (`facp`, "Field ammo point"; 8 x 8 cells, 800 HP, a worker or the bulldozer, 15 s, 400): pallets of
  ammunition under camouflage netting and sandbags, its marked **drop zone** next to it (content `dropZone`: 12 cells south of its
  centre in its own frame, radius 6 cells). Its stock (`facp_supply`) starts **empty** and holds **6,000**; airdrops and cargo trucks
  fill it. Its side's **ground** units within **12 cells** are rearmed out of it (`groundOnly`: nothing that flies; ammunition only:
  no repair, no fuel, no reload bonus): a charge of a short stock every 2 s at 20 a charge (`roundCost`), and depot-reloaded weapons
  as at a depot at their own cost (the air defence vehicle's missile 60, one every 4 s; measured 200 pays for 3). Empty, it does
  nothing.
- **The supply stash** (`supply_stash`; 20 x 14 cells, 1,500 HP, a worker or the bulldozer, 20 s, 800): a paved drop pad (its
  zone: the west half, radius 8 cells from 5 cells west of its centre) with a small hangar holding the stock. **Storage only**
  (`storage`: it rearms nothing, gives no reload bonus, is no depot), empty when built, **15,000** at most.
- **Cargo trucks** (the Sentinel's hook-lift truck: 400 a load; since the howitzer anything from 0 to 400). **Load** (`CommandKind.LoadCargo` 40,
  `OrderKind.LoadCargo`; `Unit.LoadFrom`, `Unit.LoadLeft`) at a store whose supply is a `cargoSource` (a stash, a forward airbase, a
  main air base: free there): it drives into the store's radius, empty, and takes a full load out of the stock in its cargo's
  `unloadTicks` (3 s); while the stock holds less (counting trucks already loading) it waits beside it. **Unload** (the existing
  `Unload`) at an ammo point, a stash or a forward airbase; a store (`storage`) takes a load only on an Unload order aimed at it, so
  a full truck standing at a stash never unloads back into it (an ammo point and a depot still take loads on their own). The
  military garage's refill is unchanged; the forward airbase's garage refills a truck out of its stock. Events `TruckLoaded`,
  `TruckUnloaded`.
- **Never past the cap** (decision 316, 2026-10-24): every inflow stops at a store's cap. A pallet's excess is lost (as before); an
  airlifter's container goes in only while a whole one fits, checked every tick of its 7.5 s (as before); a truck checks for room
  when it starts its 3 s unload and, should the room have shrunk by the end (a pallet landed meanwhile), pours in only what fits
  and **keeps the rest**, waiting beside the store as any truck whose load doesn't fit. Before, it poured the whole load in:
  measured on the old code, an ammo point at 6,400 of 6,000 and a stash at 15,400 of 15,000; now 6,000 and 15,000 with the truck
  still holding its 400 (`NothingPushesAStorePastItsCap`; the forward airbase, `AContainerThatNoLongerFitsStaysAboard`). The mirror:
  a truck loading takes what the stock still holds at the end of its load, never driving it below 0
  (`ATruckLoadingNeverTakesTheStockBelowZero`).
- **The forward airbase** (`forward_airbase`, "Forward airfield"; a new airfield type, not the forward airfield's pieces): one
  compact structure (140 x 64 cells, 30 s, 3,000) a worker or the bulldozer places at least **120 cells** from its side's main air
  bases (content `awayFrom`: footprint to footprint, finished or a site; the other side's do not count; `World.AwayFromAllows`). Like
  the main base's core it is the pavement under the whole field (never a target) and, finished, lays its pieces: a **7-segment
  runway** of the main base's segments (140 cells, 15 wide; connectors at x 10, 70, 130; extension segments as the base's), the
  apron (`forward_airbase_apron`: its "hangar" piece, pavement) with **2 stands** (`forward_airbase_stand`: heavy-kind stations
  taking **any class S to XL**, nose in, out by pushback; 26 x 24 cells), and the **field garage** (`forward_airbase_hut`: the tan
  fabric clamshell, 24 x 14 cells, the type's facility: a target piece with rubble and repair). No radar, **no extra rows**
  (`rows.extra` false), and **no production**: its garage's production block has 0 bays and no tags (doors only, for its service
  block); `Produce` to any of its pieces is ignored. The jets, the attack jet, the bombers and the airlifter take its runway; the
  helicopters do not take its stands (they keep to helipads and the shelters and stands of the other fields).
  - **Service:** 2 aircraft at once on its stands (refuel 1x, rearm and repair 2x: the main base's heavy bay rates) and 2 vehicles
    at once in its garage (reload 5 s, repair 10 s, a truck's load 10 s: the military garage's), the garage's doors turned with the
    field. With the garage down the stands only refuel (`facilityDown: "refuel"`).
  - **Its stock** (`forward_airbase_stock` on the core; `World.StockOf` on any piece, `World.AirfieldStore`): **8,000** when built
    or spawned, **20,000** at most. **Every service draws on it**: a round (or a flare salvo) when it is loaded, 20 (a weapon's
    depot cost when it has one), else the round waits; fuel at 200 a full tank and HP at 10 per 100, pro rata, while the stock is
    above 0 (`AirOps` remainders); in the garage each part is paid as it starts (the reload, the repair, a truck's load) and a part
    the stock cannot pay is skipped. **Empty, it only parks**: an aircraft waits there unserviced (it may be ordered off), a vehicle
    comes out as it went in. Measured: two jets rearmed and repaired in 10 s for 156; three air defence vehicles (10 missiles, half
    HP) through its 2 bays, at most 2 inside at once, 614 each.
  - Refilled by an airlifter landing (Deliver) and by cargo trucks (Unload); trucks load there too.
- **The main air base**: `airbase_supply` (unlimited, `storage`, `cargoSource`): trucks load there free within 30 cells of its
  centre; its stations draw on nothing.
- **For renderers and the HUD:** `World.LogisticsEvents` (`LogisticsEvent`: kind, aircraft or truck, store, where, amount into the
  store, lost, pallet), `World.StockOf(u)` (`StoreStock`: stock, cap; 0 = unlimited), `World.DropZoneOf(u)` (centre, radius),
  `World.CargoAboard(u)` (pallets, supply), `World.AirdropWeapon(u)`, `World.AirfieldStore(af)`, `Unit.AirdropAt`,
  `AirOps.UnloadLeft` (with `airdrop.unloadTicks`), `AirOps.RecallTo`, `Unit.LoadFrom` / `LoadLeft`, `HomeReason.Deliver`; the
  airdrop's run and fall as a bomber's (`Unit.Strike`, `World.BombReleases`, `World.Projectiles`). The ramp, the visor, the
  parachutes, the burning lost pallets and the fuel bladders are the client's.
- **Follow-ups:** no capturing (owner: later); no skirmish AI use; no automatic truck shuttle (a truck takes one order at a time);
  Shift-queued airdrops are not kept; a drop zone takes pallets even when its store is full (they are lost).

### Massive bomb plane (MC-130 type with one MOAB type bomb, 2026-10-30)

The owner (2026-10-28 to 30; the game repo's `docs/plans/moab-spec.md`): a big, slow cargo plane that drops **one huge bomb**
(not several), **not stealthy**, **slow but long range** (it can reach any target and loiter: its threat alone deters), **no
parachute** (the bomb falls straight down), **no limit** on how many, **the blast starts fires**; a two-bomb heavy upgrade is
later; every number is tuned with the owner after the first test. The Sentinel's answer to the Resistance's bases and tunnel
networks; its counter is plain: hold your sky and it never arrives. A tier 2 pick of the Sentinel's aircraft pool
(`doctrine-picks.md`); the pick lobby does not exist yet, so it is an ordinary unit of the faction for now. Branch `moab`, job MBe.
Content: `archetypes/sentinel.json` (`mc130`), `components/sentinel.json` (`moab_bomb`), `warheads.json` (`moab`),
`factions/sentinel.json`. Engine (generic, opt-in): the delivery fields `straight` and `warn`, the weapon block `blast`
(`Sim/Systems/RingBlastSystem.cs`: rings, garrisons, tunnels by radius, forest flattening, fire), `TerrainMap.Flatten` /
`Flattened`, `World.Flattens`; the bunker buster's tunnel rule is now `CollapseTunnels(radius)` (`PenetratorSystem.cs`). Tests
`MoabTests` (11). Decisions 744 to 758.

- **The plane** (`mc130`, decisions 744, 745, 755, 756): class XL, the gunship's airframe (span 40 m, length 30 m) and landing,
  built by the main air base's hangar (120 s, 5,000; placeholders; measured: two queued, both out at 120 s through its two bays)
  and parked only in a heavy bay; not in the heavy bombers' exclusive group: no limit. No signature block: every sensor detects,
  locks and kills it at full range. Cruise **11 cells/s** (the gunship 12, the airlifter 13, the bombers 14 to 18), 11.5 on full
  power, stall 5, **300 m** up (the airlifter's height, inside the AA soldier's 320 m ceiling), **40 minutes** of fuel (the
  gunship's 45 stays the longest), the heavies' 65-cell fly zone. 1,800 HP and the `airlifter` air class (a big four-engine transport:
  every interceptor that engages the airlifter engages it; 4 air defence missile hits, 3 of the S-400 type's). 30 flare salvos,
  two a missile, no break; no guns, no air weapons, no Guard (it never picks targets). The ramp and the props are the client's.
- **The drop** (decisions 746 to 748): an attack-ground order (or a line's first point) on a ground point (`pointsOnly`): the
  heavies' attack run (an 80-cell align leg: a straight run in), the release over the point, then the break; spent, it goes home
  to rearm (decision 143's rule; the bomb in 15 s in a heavy bay). **Straight fall** (delivery `straight`): the bomb loses all its
  horizontal speed as it leaves (`retard` 1000 permille, so the release solution releases it over the aim: measured 0.1 cells from
  the point, 0.0 from under the release) and falls at the real gravity (`gravity` 98: 9.8 m/s2; the other bombs' compressed 70
  m/s2), **7.8 s** from 300 m (measured 156 ticks). A straight bomb skips the run's nose-angle abort (the plane is over its aim at
  the release); the "past it by more than the window" abort still sends it round again. **The warning** (delivery `warn`): from the
  release to the impact every enemy player sees it in `World.IncomingStrikes` (its point, the light ring's 75-cell radius, the impact
  tick; measured 156 ticks, the predicted impact tick exact) with `IncomingEvents` Warned at the release and Struck at the impact;
  before the release the plane itself is the warning (seen by every sensor). **Shot down before the release, nothing falls**
  (`ShotDownBeforeTheReleaseNothingFalls`); a bomb already released falls on.
- **The blast** (weapon `blast`, decisions 749, 750; 3,000 damage, the `moab` warhead's table): instead of the splash falloff,
  every unit on the ground within 75 cells (a structure by its nearest footprint point) takes the ring it is in. **Core, 0 to 15
  cells:** destroyed whatever its armour, with whoever is inside (the bunker buster's rule: a 100,000-HP tank, a panel block and its
  garrison). **Heavy, 15 to 40 cells:** 40% (1,200; flat): soldiers, cars and trucks die, buildings wrecked (2,400: a villa and a
  panel block fall, a vehicle garage is left at 100 of 2,500), tanks badly hurt (a bomb hits directional armour on top, 150%:
  an Abrams at 30 cells kept 700 of 2,500). **Light, 40 to 75 cells:** 10% (300) at 40 cells fading linearly to nothing at 75: a
  soldier at 45 cells dies, at 72 keeps 30 of 60; a brick block at 63 keeps 1,300 of 1,600; an Abrams at 55 keeps 2,230; beyond 75
  untouched. A crest halves it outside the core (any splash's rule); friends are hurt too; flying aircraft untouched.
- **Garrisons** (decision 751): a building the blast brings down takes its garrison with it (no collapse roll); one left standing
  shelters it (the warhead is in no garrison's occupant warheads).
- **Tunnels** (decision 752; weapon `blast.tunnels` 40 cells): every tunnel entrance within 40 cells (by its footprint, any
  owner's) collapses into rubble and every unit underground at it dies, as does one on a passage at a place within 40 cells; the
  network's farther entrances and their units live (measured: entrances at 0, 20 and 36 cells collapsed, 7 killed underground with
  the one on the passage 39 cells off; the entrances at 47 and 60 cells and the one on the passage 41.5 cells off untouched).
- **Forest** (decision 753; `blast.flatten` 40 cells, `flattenTo` 1): every forest cell whose centre is within 40 cells drops to
  scrub and its standing trunk is felled (a stump, no fallen log): no cover from the air there (concealment reads grove and dense
  canopy only); the scrub still burns. Measured over dense forest: 5,018 cells flattened, 1,062 trunks felled, nothing beyond 41
  cells. The terrain's digest reads a flattened cell's forest as it was (as burnt and blasted ones); the world hashes the
  flattenings (`World.Flattens`, marker 0xF1A7, only once one happened). Infantry routing reads it as before
  (`thinnedForestAsBefore`).
- **Fire** (decision 754): the warhead's fire block lights every forest cell in the core, the weapon's own 10 to 20 more out to 40
  cells, all spreading at the fire rules' chances, after the flattening; the fire rules' cap holds (over dense forest the core
  alone fills it: measured 150 burning, none beyond 40 cells). Buildings have no fire state in the engine: the client draws the
  ruins of the buildings it brought down burning.
- **For renderers:** `World.BombReleases` and `Projectiles` (the release and the straight fall; the plane's `Unit.Strike` phase for
  the ramp), `IncomingStrikes` / `IncomingEvents` (the warning rings), `BombImpacts` (a weapon with `Effect.Blast`: the MOAB's
  look; tier 7), `RingBlast.Rings` (the three radii), `TunnelCollapses` / `TunnelKills`, `Flattens` and `TerrainMap.Flattened`
  (flattened trees), `Stumps`, `Fires`.
- **Follow-ups:** the tier 2 pick (the doctrine lobby); the two-bomb heavy upgrade (owner: later); the numbers after the owner's
  first test; skirmish AI use; a building fire state (decision 754).

### Helicopters

The Sentinel's three helicopters and the helipad (the night of 2026-10-21; the owner's spec; branch `helicopters`).
Content: `archetypes/sentinel.json` (`attack_helicopter`, `armored_helicopter`, `light_helicopter`, `helipad`),
`components/sentinel.json` (`heli_cannon`, `heli_cannon_he`, `heli_missiles`, `heli_missiles_light`, `heli_rocket_pods`,
`heli_miniguns`, `light_heli_rockets`), `warheads.json` (`heli_cannon`, `heli_cannon_he`, `heli_missile`),
`air_classes.json` (`attackheli`, `armoredheli`, `lightheli`, appended), every AA and air-to-air weapon's values for
them. Code: `Model/Rotary.cs` (`RotarySpec`, `OutOfControlSpec`, `HeliCrewSpec`, `RidersSpec`, `HelipadSpec`),
`Sim/Heli.cs` (`HeliState`, `Unit.Heli`, hashed only for helicopters; the queries' records),
`Sim/Systems/HeliFlightSystem.cs` (the controller, the rotary kinematics, the ground, out of control's flight),
`HeliCombatSystem.cs` (targets, weapons, the pop-up), `HeliPadSystem.cs` (pads, homes, service, production into a pad,
riders, out of control and the crashes), `HeliCommandSystem.cs` (orders). Tests: `HelicopterTests` (29). Bench:
`SimRun -bench-heli N`. Every number is content (placeholders unless the owner gave it); the calls taken without the owner
are in the table at the end ("Decisions taken tonight").

- **The rotary flight** (`flight.kind: "rotary"`, `flight.rotary`). Three layers as every aircraft: the order picks a
  goal, a nose direction and a height; the controller turns the goal into a wanted velocity it can still stop from (an
  arrival law at 80% of its acceleration); the kinematics: the nose yaws at most its `yawRate` a tick (it **turns on
  the spot**: 180 degrees in 2 s for the attack helicopter), the wanted velocity is clamped in its body frame (forward
  up to its top speed, **sideways and backwards** up to its `sideSpeed`), the velocity changes by at most its
  acceleration a tick (it speeds up and slows within its limits; speeds raw a second with the tick's remainder kept), it
  climbs at its climb rate and sinks at its `descentRate`. **Terrain following:** in transit the height planner (E12)
  over a 5 s look-ahead at its flight height (30 m), hovering or creeping the exact ground under it (so it stays low
  behind a crest). Measured: across a 40 m ridge at its dash speed 20.5 to 59.4 m over the ground (`ItFollowsTheTerrainLow`).
  It never needs a runway (`aircraft.landing: "vertical"`): it lifts off straight up after its rotor spools up
  (`spool`: 4 s, the light helicopter 3) and lands straight down on a helipad, an airfield's stand or shelter, or open
  ground. Fuel burns only in the air. It stays on the map (no fly zone band). A helicopter put down in flight (a test,
  the sandbox) starts at its height, hovering.
- **The three** (owner's numbers; 1 cell = 2 m, so 40 m/s = 20 cells/s):

  | | Attack (`attack_helicopter`, AH-64 type) | Armoured (`armored_helicopter`, Mi-28 type) | Light (`light_helicopter`, AH-6 type) |
  | --- | --- | --- | --- |
  | Cruise / top / sideways (cells/s) | 20 / 37.5 / 5 | 17 / 32 / 5 (HB; was 19 / 36) | 25 / 40 / 6 |
  | Yaw, acceleration, climb | 90 deg/s, 6 cells/s2, 15 m/s | 70 (HB; was 80), 5.5, 12 (HB; was 15) | 120, 8, 12 |
  | HP; air class | 600; `attackheli` | 650; `armoredheli` | 300; `lightheli` |
  | Weapons | 30 mm chin cannon (turret, 60 bursts), 10 anti-tank missiles (HB; 500, 40 cells, radar-cued, a ripple at up to 4), 2 rocket pods (38) | HE cannon (+30% on soldiers, -10% on vehicles), 8 missiles (HB: 375, 28 cells, command guidance), 2 pods | 2 miniguns (one weapon, 100 bursts), 8 light rockets (50 a hit on a truck) |
  | Sensors | sight 35 cells, mast radar: enemy vehicles within 120 cells for its side | sight 30 (HB; was 35) | sight 35 |
  | Flares | 8 salvos | 8 | 6 |
  | Crew (survive a crash landing) | 2 (50%) | 2 (70%) | the pilot (50%) and 3 riders (40%) |
  | Fuel; cost; build | 10 min; 3,500; 60 s | 10 min; 3,000; 55 s | 8 min; 1,500; 30 s |
- **Weapons** (ground weapons fired by the helicopter system from its height; never picked by ordinary combat). Each tick
  a helicopter in flight fires every weapon with ammunition that is good against its target (at least a third of the
  best's damage a second; a missile only when it is the best: no missile at a soldier), reloaded, within its range
  (slant), within its **mount's cone** of the nose (`cone`: the chin cannon 110 degrees either side, missiles 15, pods
  10, the miniguns 15) and with a **line of sight** over the terrain and the canopy. Guns hit at once (scattered, a
  small splash); pods fire their salvo as straight lines from its height; missiles are **fire and forget**
  (`guided.fireAndForget`: their own seeker, no operator, no line from the shooter after launch), still stopped by a
  crest (HB: the attack helicopter's lock on after launch at its radar's tracks and loft over it; the armoured
  helicopter's are command-guided: see "HB" below). Ranges are game ranges (the Kornet's 28.8 cells, a rifle's 5): cannon 20, pods 25, missiles 40, miniguns 15,
  light rockets 20. Measured (`EachHelicopterFightsSoldiersAndVehiclesWithTheRightWeapons`): six riflemen fall to the
  attack helicopter in 9.9 s (cannon and pods, no missile), to the armoured one in 8.4 s; three technicals to three
  missiles in 5.7 to 6.2 s; the light helicopter clears the riflemen in 9.6 s with its miniguns and needs 19.9 s and
  its 8 rockets for the technicals; six light rockets bring down the 300-HP heavy rocket truck.
- **Targets and orders.** An Attack order's unit (seen by its side), an Attack-ground order's point (pods and guns, never
  a missile); Guard (a post or a unit: within its `leash`, 40 cells, of the post, then back over it), Attack-move and
  Patrol (`AirTask`; Search and Roam fly a loop round the area's corners) pick the best target within the leash plus
  their weapons' reach (the best weapon's damage a second over the distance, ties to the lower ID, scanned twice a
  second staggered by ID); idle in a firing stance (Guard, Hold ground, Aggressive) it fights what comes within its
  weapons' reach from where it hovers, nose on it, without chasing (Hold fire: never). Move ends a task; Stop hovers
  where it is (landed: stays down). Orders refused say why (`World.OrderRefusals`: `NotVisible`, `NoAmmo`,
  `NoWeaponForTarget`). A helicopter out of control takes no orders.
- **The flying AI: terrain masking and the pop-up** (`HeliAttack`). A missile attack beyond its guns' reach looks for
  cover first: points on 9 bearings (0 to 60 degrees either side of the line toward it) at its standoff (`standoff`
  85% of the missile's range) and at three quarters of that, within its `coverSearch` (80 cells), where the line of
  sight to the target is blocked at its hide height (`hideHeight` 5 m) by at least 2 m (a crest or woods: terrain and
  canopy) and clears within its `popMax` (60 m) with 2 m to spare; the best: the lowest pop-up plus a quarter of the way
  there. It flies there low (`ToCover`), **hides** at least a second until a missile is ready, **rises** until it has
  the line, **fires**, holds half a second and **ducks**, and again; it looks for new cover when the target moved 8
  cells, or a rise did not clear in 10 s. With no cover (a flat map, open ground) it hovers at its standoff, nose on the
  target (`Standoff`). Guns and rockets: a **run** to 75% of the weapon's range (`gunRange`) at its attack height (15 m;
  the light helicopter 12), then it sidesteps round the target (its goal swung 20 degrees round it, one way by its ID),
  nose on it. Measured before HB (`ItHidesBehindARidgeAndFiresOverIt`: a 30 m ridge, a technical 20 cells beyond the
  crest): it hid at 22.6 m (the line needed 35.3 m), rose and fired at 35.4 m, the missile came over the crest and
  destroyed the technical, then it ducked; without cover it fired from 39.1 cells. Since HB the attack helicopter fires
  that shot from its hiding place (below).
- **HB, the helicopter balance** (owner 2026-10-29; the game repo's `docs/plans/heli-balance-spec.md`; branch
  `heli-balance`; decisions 736 to 743). The Mi-28 type was the better buy (cheaper, tougher, missiles only 10% weaker);
  now the **AH-64 type is the special pick**, the hunter-killer that finds armour on radar and kills it from cover, and
  the **Mi-28 type the cheap armoured brawler** that must get close and needs spotters. Costs unchanged (3,500 / 60 s and
  3,000 / 55 s).
  - **Radar-cued fire** (`guided.lockAfterLaunch`, with `fireAndForget`; the attack helicopter's `heli_missiles`). At a
    vehicle its **own** mast radar tracks (`World.OwnRadarTrack`: the rule its side's radar spotting uses, within 120
    cells, vehicles only, none in smoke), the missile needs no line of sight: the helicopter fires from its hiding place
    behind the crest without rising (the pop-up's Hide is its firing position), and the missile is **lofted**
    (`Projectile.Lofted`): no crest stops it. Elsewhere (a target its radar does not track) it pops up as before.
    Measured (`TheApacheFiresFromCoverAtItsOwnRadarsTrackWithoutRising`, fog on, a 30 m ridge, a technical 20 cells
    beyond it): launched at 23.1 m with the line needing 35.2 m, lofted, the technical destroyed, no rise.
  - **The ripple** (a weapon's `ripple`: `targets` 4, `interval` 5 ticks, `cone` 30 degrees): after a missile at its
    target it fires one each at up to 3 more **different** vehicles, the nearest first (ties to the lower ID), for which
    the missile is its best weapon, within range and the ripple's cone (its seeker turns onto a target beyond the mount's
    15 degrees), seen or on its own radar's track (hidden: only the latter), 0.25 s apart; the
    2 s reload runs from the first. Fewer such vehicles end it early; alone, the target gets one missile a pop-up. The
    pop-up holds (`Fire`) until the ripple ends. Measured (`ItsRippleTakesUpToFourVehiclesAPopUp`, six infantry
    carriers): launches at 0, 5, 10, 15 ticks at four different carriers, the next at 40; across a line of five carriers
    6 cells apart (`ItsRippleTurnsOntoVehiclesBeyondTheMountsCone`) its rounds went 0, 9.3, 9.6 and 19.1 degrees off the nose.
  - **Command guidance** (the Mi-28 type's `heli_missiles_light`: no `fireAndForget`). The guided-missile operator rule
    (`Guidance`), for a helicopter from its height: the missile steers while its helicopter is alive, flying (not
    landed, not out of control) and has a line of sight to the target over the terrain and the canopy (a smoke screen
    across the line breaks it, as for a ground operator); the helicopter (`Unit.Guiding`) holds its pop-up exposed
    until the missile ends and guides one at a time; its reload runs from the launch. Brought down at the launch, its
    missile goes wild (`ACommandGuidedMissileGoesWildWhenItsHelicopterFalls`: the attack helicopter's still hits).
  - **The Mi-28 type's numbers:** missiles 375 (75% of 500), 28 cells (70% of 40: its standoff, 85% of that, is 23.8
    cells against 34), sight 30 cells, cruise 17 cells/s (34 m/s), dash 32 (64 m/s), yaw 70 degrees a second, climb
    12 m/s. Its standoff lies within its rocket pods' 25 cells, so with pods it fights a missile attack from a run
    (charging in to 21 cells) and holds the line there; only with its pods spent does it hide and pop up (then holds
    the line in the open: `WithoutARadarTheHavocPopsUpAndHoldsTheLineUntilImpact`).
  - **Two fixes the shorter missile needed** (every helicopter): the cover search keeps only points whose slant from
    the pop-up height is within the missile's range, and a standoff hover with the target hidden at its attack height
    hovers as high as the line needs (at most `popMax`) instead of hovering blind.
  - **The duel** (`TheDuelTheApacheKillsMoreTanksFromCoverThanTheHavoc`: fog on, four Bookers parked 18 to 24 cells
    beyond a 30 m ridge, each type attack-moving onto them alone): the attack helicopter destroyed all four in 13.1 s
    with 8 missiles, exposed (a line of sight to a live Booker) 1.1 s; the armoured one all four in 21.9 s with 8
    missiles, exposed 17.2 s.
  - Hashing: the ripple in `HeliState` only while one runs, `Projectile.Lofted` only when set (decision 743). Tests:
    `HeliBalanceTests` (18), `HelicopterTests` updated (the pop-up test is now the radar-cued shot).
- **Toughness** (the interception model, per class; owner: hits that bring it down). The AA soldier's Igla takes 25%
  (attack), 20% (armoured) or 50% (light) a hit: **4 / 5 / 2 hits**; the air defence vehicle 34%, 26%, 100%: **3 / 4 / 1**
  (`HitsBringItDownOutOfControl`); its best kill chances 45 to 50% (Igla) and 70 to 75% (vehicle), judged against 30
  cells/s. A **Patriot-type hit destroys any helicopter outright** in the air (`intercept.outright`; 60% at best,
  radar-linked). The jets' missiles engage them (a hit: out of control) and their guns (45 / 25 / 50% a burst). Machine
  guns and cannons that reach low air wear them down, the armoured one less (the heavy MG's burst chance 50 / 15 / 55%);
  the heavy MG's 6 cells of range reach a helicopter only within about 12 m of slant (a hovering one low beside it).
  Flares decoy IR missiles (8 salvos, 50% left each). On the ground it has `parked_aircraft` armour and is a ground
  unit for the fog, splash only (as a parked jet).
- **Out of control** (the owner's standard for every helicopter). The hit that would bring it down (its HP to 0 in the
  air, any weapon) puts it out of control instead: 10% of its max HP (`outOfControl.hpPercent`), draining evenly to
  nothing over 30 s (`ticks`); its order, errand and attack are gone and no player order reaches it; it flies itself
  to the nearest friendly pad (a free helipad or airfield station first; with none free the nearest taken one, to set
  down beside it) at half its cruise (`speedPercent`), its nose spinning (`spinRate` 120 degrees a second, a seeded way;
  the light helicopter 150), sinking evenly from where it was toward 5 m (`endHeight`). Over the pad it comes down at
  twice its descent rate and **lands hard**: it survives with what HP it has, the pad services it as on a Rearm (fuel,
  ammunition, flares, HP) and holds it (`OutOfControlItReachesAPadAndSurvives`). **Any further hit** in that state (its
  HP below the drain's) **destroys it in the air** (its wreck falls; nobody survives). When the time runs out it
  **crash-lands** where it is (a wreck from its low height: the crash warhead where it meets the ground), its crew and
  riders each roll survival (`crew.survive`, `riders.survive`: crew step out as pilots on the nearest free cell). No pad
  at all: it drifts down where it is and crash-lands. Running dry in the air is a crash landing; destroyed on the ground
  the crew and riders roll survival too. Measured: 30.0 s to the crash landing; crew 6 of 24 survived over 12 seeds
  (50% in content), riders 17 of 36 (40%), the pilot 8 of 12. For the client: `World.HeliStatusOf` (phase
  `OutOfControl`, ticks left, the spin rate signed, the sink rate), `World.HeliCrashes` (each ending's kind:
  `AirDestroyed`, `CrashLanded`, `HardLanded`, `DestroyedOnGround`; where, its height, the survivors' unit IDs, how many
  were lost), `World.Wrecks` (the falling wreck), `AirStatusOf` (`OutOfControl`, its time left in percent).
- **Pads, homes and service** (the jets' rules). Its pads: its side's helipads and the stands and shelters of its side's
  airfields that take its class (S; never a heavy bay). One helicopter a pad (a helicopter bound for it or on it, an
  airfield station's `Occupant` / `ReservedBy` so the jets and drones leave it alone, a helipad's build). **Home on its
  own only to refuel** (bingo: the fuel home at cruise plus a 5 s landing and its reserve): it refuels only, then flies
  back to its post (or its order). **Rearm** (29): home to the nearest free pad (or the one named), fuel, ammunition,
  flares and HP, then it resumes its task (`AirMission`; with none it waits there ready). **Return to base** (28): home,
  refuelled, held. Service: its own 1x rates (`aircraft.service`: a tank in 20 s, a round in 0.5 s, 15 HP/s; the light
  helicopter 10) times the pad's percents (helipad: fuel 1x, rearm and repair 2x; a station: its airfield block under the
  airfield's rules). Measured: a Rearm visit (half fuel, no missiles, half HP) serviced in 15 s on a helipad.
- **The helipad** (`helipad`: 12 x 12 cells, 1,500 HP, built by a worker or the bulldozer in 30 s, 800; placeholders).
  Pavement (soldiers walk across it to board, shots pass over it, splash hurts it). It **builds one helicopter onto
  itself** (`production`: tags `helicopter`, one at a time, `intoStation`): landed on it, its rotor stopped, full, held
  until its first order; refused while a helicopter is bound for it or on it. It parks, refuels, rearms and repairs the
  one on it (`World.PadStatusOf`: occupant, inbound, the build and its progress, servicing). **The main air base's
  hangar builds helicopters too**, onto a free stand (then a shelter) of its base (never a heavy bay); they are serviced
  on stands and shelters by the airfield's rules.
- **Riders** (the light helicopter's benches: `rotary.riders`). Three friendly soldiers (any infantry) ride outside
  beside the pilot (inside the unit: unseen, untargetable, **they never fire**). Enter (soldiers, the helicopter as the
  target) walks them to it; an idle one in the air lands where it is to take them (Load, `CommandKind.HeliLoad` 35, lands
  at a point and waits); they climb aboard within 3 cells of it on the ground. **Unload** (`HeliUnload` 36): it flies to
  the point and drops them one every 2 s (`dropTicks`) onto the free cells round it, by landing or (`On`) by hovering 3 m
  up (`dropHeight`) without touching down; Exit drops them where it is. Measured: hover drop at 3.0 m, riders out 2 s
  apart. `World.RidersOf`, `HeliStatusOf.Riders`.
- **Fog and vision.** Helicopters see like low aircraft (an air viewer's whole disc, 35 cells); enemies spot them as air
  units (by distance; air defence by its slant air vision x signature). Landed they are ground units (a ground eye, the
  fog's line of sight). Hiding behind a crest blocks lines of fire and missiles, not detection (decision row H-9).
- **For renderers.** `World.HeliStatusOf(u)` (`HeliStatus`: phase, attack step, target, heading vector apart from the
  velocity, velocity, climb, height over the ground, rotor speed permille, pad, riders and seats, out of control's clock
  and spin and sink rates, errand), `HeliState.Yaw` (this tick's yaw), `World.PadStatusOf`, `World.RidersOf`,
  `World.HeliCrashes`, `World.AirStatusOf` (new activities `OutOfControl`, `Boarding`, `Unloading`, `Hovering`,
  `Vertical`), the projectiles (`Projectile.Altitude` at launch), `World.Countermeasures`, `World.AirBursts`,
  `World.Wrecks`, `World.AirLosses`.
- **Performance.** `SimRun -bench-heli 100`: 100 helicopters on attack-move into a 300-unit army with AA on a hilly
  256-cell map add 1.6 ms to the tick p99 and 0.48 ms to the mean (M3 Pro, 1,200 ticks). Content without helicopters pays
  nothing (every pass is off until one is added) and hashes as before.

#### Decisions taken tonight

| # | Decision (as built) | Alternative or lever |
| --- | --- | --- |
| H-1 | A rotary flight is a flight kind of the air framework (air classes, AA, signatures, flares, wrecks) with its own controller, kinematics and orders; `AirTask` Patrol and Guard reuse `AirMission`, Search and Roam are flown as a loop round the area's corners | the missions' lanes and roam sectors for helicopters |
| H-2 | The owner's m/s at 2 m a cell (cruise 40 m/s = 20 cells/s); a Move order flies at its top speed, everything else at cruise | cruise everywhere; top speed only on a dash order |
| H-3 | Heights: 30 m in transit, 15 m on gun runs (12 the light one), 5 m hiding, pop-ups to 60 m (50); climb 15 m/s (12 the light one; 10 lost 20 m over a steep ridge at its top speed); the planner's look-ahead 5 s | lower transit for more masking |
| H-4 | Game ranges: cannon 20 cells, pods 25, missiles 40, miniguns 15, light rockets 20 (the Kornet 28.8, a rifle 5) | the owner's real-world ranges scaled differently |
| H-5 | Weapon use: every weapon with at least a third of the best's damage a second against the target fires; a missile only when it is the best | priorities per weapon in content |
| H-6 | Its missiles are fire and forget (`guided.fireAndForget`, new): no operator, no line after launch; crests still stop them | operator-guided (SACLOS), exposed until impact |
| H-7 | Helicopters fight like ground units by stance: idle in Guard (the default) they engage what is within their weapons' reach without chasing; Guard, attack-move and patrol pick within a 40-cell leash (the jets pick only on Guard) | the jets' rule (only Guard picks) |
| H-8 | Cover search: 9 bearings x 2 rings (85% and 64% of the missile's range), hidden by 2 m at its hide height, a 2 m pop margin, scored by the pop-up height plus a quarter of the way; re-planned when the target moves 8 cells | more rings; a cover memory; woods only |
| H-9 | Terrain masking blocks fire, not detection: helicopters are spotted as air units (by distance; air defence by slant air vision) even behind a crest | a line-of-sight spotting rule for low helicopters |
| H-10 | The armoured helicopter's armour is its class's low gun hit chances (heavy MG 15% a burst against 50%), not a new armour class | an `armored_aircraft` armour class in every warhead |
| H-11 | The heavy MG (6 cells, 40 m ceiling) is unchanged: it reaches a helicopter only within about 12 m of slant | a longer air reach for the heavy MG (a golden change) |
| H-12 | Hits to out of control by `hitDamage`: Igla 25 / 20 / 50%, air defence missile 34 / 26 / 100%; the Patriot type `outright` (new) at 60% best; the jets' missiles bring it down, their guns 45 / 25 / 50% | per-class hit rules (`hits`) |
| H-13 | Out of control: 10% HP draining over 30 s, half cruise, spin 120 deg/s (150), sinking to 5 m; a free pad first, else beside the nearest taken one; no pad: it crash-lands where it drifts; destroyed in the air: nobody survives; running dry: a crash landing; a hard landing is serviced as a Rearm and held | a pad's own repair only on order |
| H-14 | Crew survivors step out as `pilot` soldiers; the light helicopter's crew is its pilot (one) beside 3 riders | two crew |
| H-15 | Riders: any infantry, board within 3 cells of it on the ground, an idle one in the air lands for them; drop one every 2 s; a hover drop 3 m up | fast-rope at 10 m |
| H-16 | The helipad is pavement (walkable, splash only), 1,500 HP, refuel 1x, rearm and repair 2x, one build at a time | a targetable pad structure |
| H-17 | The main air base builds helicopters into a stand, then a shelter (never a heavy bay); they take any station of their class (S) at any of their side's airfields | dedicated helicopter places on the base |
| H-18 | After a Rearm with no task it waits on the pad, ready; a refuel stop flies back to its post | resume the last order |
| H-19 | Rotor spool-up 4 s (3 s) before lift-off; it runs down 2 s after landing; fuel burns only in the air | instant lift-off |
| H-20 | Fuel 10 minutes (8 the light one); service: a tank in 20 s, a round in 0.5 s, 15 HP/s (10) at 1x | longer endurance |
| H-21 | Helicopters stay on the map (no fly zone band) | the jets' band |
| H-22 | `CommandKind.HeliLoad` (35) and `HeliUnload` (36) appended; Exit to a helicopter drops its riders where it is | Enter / Exit only |

The follow-ups: the skirmish AI does not fly helicopters; the Resistance has none (helicopter against helicopter is
untested: the air-to-air weapons engage them, a helicopter has no air weapon); low helicopters are not hidden from sight
by terrain (H-9); the heavy MG's reach (H-11); rider fast-roping; the hangar's own helicopter places.

### The jets (E8 content)

| | Multirole (`multirole_jet`, F-16 type) | Air superiority (`fighter_jet`, F-22 type) |
| --- | --- | --- |
| Scale, length, span (D1) | 1.0, 15.1 m, 10 m | 0.9, 17 m, 12.2 m |
| HP; armour in the air / parked | 280; `aircraft` / `parked_aircraft` | 320; the same |
| Cruise, attack, dash, stall (cells/s, D3) | 20, 23, 28, 8.5 | 21, 24, 30, 9 |
| Corner speed / max-g turn / sustained at cruise | 15 cells/s / 8 cells (14.2 at cruise) / 16.2 (E12; was 18) | 16 / 9 (15.5) / 16 (E12; was 20) |
| Height, strike height, floor (D2; E13 D-ALT1) | 140 m (280 m at 1,024 cells, at most 320), 70 m, 10 m | 168 m (336, at most 384), 80 m, 10 m |
| Fuel (D4) | 3.5 min | 4 min |
| Sight, air radar | 40, 110 cells | 45, 160 cells |
| Class, signature | `multirole`, normal | `fighter`, 50 / 200 / 60 (D7) |
| Weapons | 2 IR missiles, gun (8 bursts), bombs preset (D18): 4 x 500 lb or 2 x 2,000 lb | 6 radar + 2 IR missiles, gun (10 bursts), 2 glide bombs |
| Countermeasures | 8 flares, 6 chaff | 4, 4 |
| Guard default | vehicles, structures, drones | aircraft, drones |
| Airfield | class M, 65 / 45 cells to land / take off, corridor 60 | class M, 70 / 50, corridor 60 |
| Service at 1x | tank 25 s, a round 5 s, 12 HP/s | tank 25 s, a round 3.5 s, 12 HP/s |
| Build | facility, 45 s, 2,000 | facility, 50 s, 2,400 |

Weapons: `aam_ir` 40 cells, 55 cells/s, a 0.5 s lock, a 30-degree cone; `aam_radar` 110 cells, 60 cells/s, a 1 s lock, a
60-degree cone; guns 90 damage through `air_gun`, 15 cells (E13; 9 before), a burst a second, a 10-degree cone; `bombs_500lb` 700 (rings
2 / 5 cells, small crater) and `bombs_2000lb` 1,300 (3 / 8 cells, large crater), both a 35-degree dive from a 16-cell
IP; `glide_bombs` 350, level from 70 m, guided 95 / 75%. Every jet weapon is `noResupply` (only a station's rearm
refills it). The pilot (`pilot`): 45 HP, speed 18, 5 cells of sight, a compact rifle (8 damage, 4 cells), never trained.

### Content fields

| Where | Fields | Since |
| --- | --- | --- |
| `air_classes.json` | `airClasses`: `id`, `kind` (`projectile` / `aircraft`) | E5 |
| archetype `flight` | `kind` (`drone` / `fixedWing`), `height`, `climbRate`, `launchHeight`, `orbitRadius`, `turnRate` (drones), `interceptible` (its class), `reserve`, `recoverRange`, `turnaround`, `relaunch`, `flyZoneMargin` (E12; `edgeMargin` retired, ignored) | scout drone, E3, E4, E12 |
| `flight.fixedWing` | `dash`, `attack`, `minSpeed`, `cornerSpeed`, `cornerRadius`, `sustainedRadius`, `accel`, `gravity`, `diveRate`, `floor`, `ingressHeight`, `lookAhead`, `separation`, `burn` (`idle`, `cruise`, `military`, `afterburner`) | E4 |
| archetype `mission` | `huntLeash`, `guard` (default filter kinds) | E3 |
| archetype `signature` | `detect`, `lock`, `kill` | E5 |
| archetype `sensors`, loadout `sensors` | what an air unit looks under trees with: `camera`, `thermal`, `groundRadar` (flying archetypes; none listed: a camera; a loadout without one: the archetype's) | concealment |
| `concealment.json` | `concealment`: `infantry` and `vehicle` (`still`, `moving`: a camera's reach per forest density, tenths of a cell, -1 as in the open, 0 hidden), `fireReveal`, `thermal` (`engineTicks`, `movingInfantry` per density), `heliLow` (`height`, `bonus`) | concealment |
| archetype `countermeasures` | `kind`, `salvos`, `vs`, `pk`, `window`, `perMissile`, `rearmTicks` | E5, E6 |
| archetype `evade` | `breakG`, `warn`, `maxTicks` | E6 |
| archetype `attackRun` | `align`, `breakTurn`, `break`, `extend`, `releaseWindow`, `offset` | E13 |
| archetype `element` | `slots` (`back`, `aside`), `stagger` | E13 |
| archetype `pursuit` | `separation`, `breakOff`, `reattack`, `climb`, `maxTicks` | E13 |
| flight `heightByMap` | `from`, `to`, `height`, `max` | E13 |
| countermeasure | `breakPk` | E13 |
| archetype `airBody` | `class`, `height` (an Air-layer unit without a flight as an air target) | E6 |
| archetype `loss` | `crash` (`warhead`, `damage`, `splash`, `gravity`, `drag`, `breakUp`, `breakUpDrag`, `runway`), `eject` (`unit`, `seatRise`, `riseTicks`, `chuteTicks`, `chuteRate`, `drift`, `driftTicks`) | E10 |
| archetype `aircraft` | M2's block plus `corridorNeed`, `approach.pattern` (`straightIn` / `overhead`), `initialDistance`, `patternHeight`, `breakSpeed`, `aerobrakeSpeed`, `speeds.militaryAccel` | E9 |
| weapon `intercept` | `classes`, `seeker`, `boresight`, `killBest`, `speedRefs`, `hitDamage`, `hits`, `lockTicks`, `fuse`, `killTail`, `killHead`, `crossHalf`, `speedRef`, `rangeFar`, `trackBonus`, `trackFull`, `killMin`, `killMax`, `leadTicks`, `launchHeight` | AA, E5, E6 |
| weapon `airGun` | `cone`, `hitBase` (per class), `crossHalf`, `rangeFar`, `strafeRange`, `strafeEvery`, `ceiling` (a ground gun) | E6, E7, E10 |
| weapon `delivery` | `profile` (`level` / `dive` / `toss`), `releaseHeight`, `diveAngle`, `tossAngle`, `ipDistance`, `scatterLevel` / `scatterDive` / `scatterToss`, `stick`, `stickSpacing`, `gravity`, `guided` (`hitIdle`, `hitMoving`, `missNear`, `missFar`) | E7 |
| facility `production` | `tags`, `bays`, `queueMax`, `intoStation` | E8 |
| weapon `delivery` (H2) | `modes` (`id`, `salvo`), `group`, `groupAside`, `retard` | H2 |
| archetype `gunship` | `radius`, `area`, `onCircle`, `bearing` | H3 |
| weapon `sideFire` | `scatter`, `tier`, `priority` (armour classes), `areaFire`, `pick` (`nearest` / `biggest`) | H3 |
| weapon `intercept` (H3) | `ceiling` (tenths of a metre over the ground under the target) | H3 |
| weapon `delivery` (H4) | `overfly` (fly on over the last drop before the break); a mode's `spread` (a carpet: one stick for the pass) | H4 |
| flight `rotary` (kind `rotary`) | `maxSpeed`, `sideSpeed`, `accel`, `yawRate`, `descentRate`, `attackHeight`, `hideHeight`, `popMax`, `standoff`, `coverSearch`, `gunRange`, `leash`, `spool`, `outOfControl` (`ticks`, `hpPercent`, `speedPercent`, `spinRate`, `endHeight`), `crew` (`count`, `survive`, `unit`), `riders` (`seats`, `tags`, `boardRange`, `dropHeight`, `dropTicks`, `survive`) | helicopters |
| weapon | `cone` (a helicopter's mount), `guided.fireAndForget`, `intercept.outright` | helicopters |
| archetype | `groundRadarVehicles` (a mast radar: vehicles only); a structure's `helipad` (`refuel`, `rearm`, `repair`) | helicopters |
| archetype | `cueLock` (the radar-cued lock percent; needs an `airVision`), `sweepTicks` (one turn of the search antenna, for renderers) | mobile radar |
| weapon `intercept` | `battery` (`linkRadius` tenths of a cell, `radarTags`, `semiActive`; never with `radarLinked`) | SA-6 launcher |
| weapon `delivery` | `precision` (no scatter, each bomb steered onto its own point, queued aims share the salvo), `aims` (queued aims, 1 to 32; 0: 8), `tier` (1 to 7; 0: by damage) | stealth bomber |
| weapon `penetrator` | `radius` (destroys everything there, kills the units in a tunnel, collapses the entrance), `craterRadius`, `craterTicks` | stealth bomber |
| root `exclusiveGroups`; archetype `exclusive` | `id`, `max`, `lockType`; the group's id | stealth bomber |
| archetype `loadouts`, `loadoutSwitch` | per mode `id`, `ammo` (rounds by component id, at most its ammo), `speed` (percent), `signature`; 2 to 4 modes (a fixed wing with an aircraft block); the ticks a change takes | stealth multirole |
| archetype `radarSpotting` | `tags` (the radars that count, any-of), `linger` (ticks), `radius` (tenths of a cell) | stealth multirole |
| facility `production` (intoStation) | `excludeTags` (now read for producers into a station too) | stealth multirole |
| warheads | armour class `aircraft`; warheads `air_gun`, `bomb`, `crash`; `bullet` vs `aircraft` | E8, E10 |

### Balance levers

| Lever | Where | What it tunes |
| --- | --- | --- |
| speed, `fixedWing` (`attack`, `dash`, `minSpeed`, `cornerSpeed`, `cornerRadius`, `sustainedRadius`, `climbRate`, `diveRate`) | archetype | how fast and agile; how long a dogfight or an AA engagement lasts |
| `height`, `ingressHeight`, `floor`, `flyZoneMargin`, `lookAhead` | `flight` | exposure to AA and to the heavy MG's 40 m, readability, map fit |
| `lifetime` (fuel), `burn`, `reserve`, `holdReserve` | archetype, `flight`, `aircraft` | time on station, how often it goes home |
| `airVision` | archetype | how far a radar or an eye detects (and spots, and cues) |
| `signature.detect / lock / kill` | archetype | stealth |
| `countermeasures`, `evade` | archetype | survival against AA, at what cost to the task |
| interceptor `classes`, `killBest`, `speedRefs`, `hitDamage`, `lockTicks`, `boresight`, `seeker`, weapon `range`, `maxFlight`, `ammo` | weapon | every AA and air-to-air missile against every class (the air defence vehicle's 220-cell `range` against jets: see the decisions) |
| `airGun.cone / hitBase / crossHalf / rangeFar / ceiling / strafeRange / strafeEvery`, damage, ammo | weapon | dogfights, strafing, the heavy MG, the ejection rate |
| `delivery.*`, bomb damage, splash, the `bomb` warhead, `runway` | weapon, warheads | bomb accuracy, stand-off, what a bomb destroys, runway craters |
| `mission.huntLeash`, `mission.guard` | archetype | how far and at what a guarding aircraft hunts |
| `loss.crash.*`, `loss.eject.*`, the `crash` warhead | archetype, warheads | what a shoot-down does on the ground; how long a pilot hangs |
| service `refuelTicks`, `rearmTicksPerRound`, `repairHpPerSecond`; station percents | `aircraft`, airfield pieces | sortie rate |
| `minLandRun`, `minTakeoffRun`, `corridorNeed`, `approach.*`, `speeds.*` | `aircraft` | which fields serve it, the pattern's size, AB or dry takeoffs |
| cost, `buildTime`, `production`, stations | archetype, facility | how many jets a player fields |

### For renderers

Read-only queries: `World.AirStatusOf(u)` (activity, mission, phase, reason, progress, queue place, fuel, rounds, target),
`World.OrbitCentreOf`, `World.ThrottleOf`, `World.FlightPhaseOf`, `World.ApproachStepOf` (`Join`, `Initial`, `Break`,
`Downwind`, `Base`, `Final`, `Glide`, `Aerobrake`, `Rollout`), `World.GearDownOf`, `World.SpeedBrakesOf`,
`World.AirTracked`, `World.SeesPoint`, `World.StrikeTier`; state `Unit.AirMission`, `Unit.Wing`, `Unit.Strike`,
`Unit.Salvos`, `World.Wrecks`, `World.Chutes`. Events of the last tick (read after `Step`): `World.AirfieldEvents`
(including `Initial`, `Break`, `GearDown`, `Perch`, `NoseDown`, `Afterburner`), `World.AirLaunches`, `World.GunBursts`
(with `FromAltitude`: ground guns too), `World.Countermeasures`, `World.AirBursts`, `World.AirLosses`,
`World.BombReleases`, `World.BombImpacts`, `World.StrafeBursts`, `World.Detonations`, `World.WreckImpacts`,
`World.EjectionEvents` (`Canopy`, `Seat`, `SeatAway`, `ChuteOpen`, `Landed` with the pilot's unit ID, `Lost`).

### Performance

`SimRun -bench-air 100` (the drones in mission modes, 100 test fixed wings, a `dogfight` of 100 real jets over the ground
army and a `strike` workload of 100 jets bombing and strafing): after E11 the dogfight's tick p99 is about 1.2 ms and the
strike's 0.5 ms (the same ground world without aircraft about 1.7 to 1.8 ms: within the plan's +1.5 ms gate). With no
aircraft the air code costs nothing measurable: every milestone passed the +5% gate on `-bench 500,10000`, and E11 left
both benches' final hashes bit-identical (tick p99 about 2.1 ms at 500 units and 15 to 17 ms at 10,000 on the M3 Pro).

### Decisions to confirm

Every decision the night took without the owner (the plan's section 8.5 recommendations taken as defaults, and the
agents' own calls where the plan was silent or could not be built as written), with the commit that took it. Each is
content data or a small rule change, never a rewrite.

| # | Decision (as built) | Alternative or lever | Commit |
| --- | --- | --- | --- |
| 1 | The Overseer station copies the Watcher station's numbers (1,200 HP, 1,500, 45 s, 24 x 16 m); both stations in the Sentinel starting army; glyph `x` | own numbers | `75cf078` (E1) |
| 2 | D10: after a Rearm the aircraft resumes its previous task | stay parked, held | `0b58df6` (E2) |
| 3 | D11: an automatic refuel stop refuels only; at a ground control station the relaunch keeps HP as well as stocks | also repair | `0b58df6` |
| 4 | A refuel stop or recall takes an open stand (shelters kept free); Rearm prefers a loading shelter | any station | `0b58df6` |
| 5 | Rearm while leaving: it comes back once it flies free; RTB on the ground still ignored (M3b) | abort the takeoff | `0b58df6` |
| 6 | Kept stocks also apply to a manual launch of the same kind (no rearm by recall and relaunch) | full stocks on a manual launch | `0b58df6` |
| 7 | `homeWhenEmpty` dropped from the Watcher's content (field ignored) | — | `0b58df6` |
| 8 | Watcher Guard default filter vehicles and structures; `huntLeash` 30 cells (all aircraft) | add infantry; another leash | `f15e671` (E3) |
| 9 | Route guard zone: within the leash of the route's legs (a one-point route: leash plus orbit radius) | — | `f15e671` |
| 10 | Stop on an aircraft with a mission ends the mission and orbits where it is | keep the mission | `f15e671` |
| 11 | An AirTask given while homing or at the airfield waits for its return (parked: leaves at once) | refuse it | `f15e671` |
| 12 | Orbit centres, waypoints, areas and lanes clamped inside the map less `edgeMargin` less the orbit radius (the plan's rule literally): on a 200-cell map a Watcher's orbit centre keeps 65 cells from the edge | less only a turn radius (looser on small maps) | `f15e671` |
| 13 | Homing and holding use a narrow edge band (a turn radius plus a second) | the full margin | `f15e671` |
| 14 | The full-rate turn toward an aim behind the beam only beyond two turn radii | always | `f15e671` |
| 15 | Sustained turn 18 cells at cruise for the multirole (the plan's 12 is tighter than its own max-g turn at cruise, 14.2: impossible) | a wider envelope | `7c3a92f` (E4) |
| 16 | Edge margin rule: at least 2 x the max-g turn at cruise plus 1 s of cruise, plus a braking look-ahead above cruise (the plan's dash rule gave 84 cells) | the dash rule | `7c3a92f` |
| 17 | No task commands a dash (afterburner only for evasion, egress, takeoff, stall recovery) | dash to targets | `7c3a92f` |
| 18 | Load factor in real g (9.81 m/s^2) on the compressed geometry (about 5.7 g at most) | scaled g | `7c3a92f` |
| 19 | An air class's value is its position in `air_classes.json` | explicit ids | `b35d3b6` (E5) |
| 20 | Jets judged against their attack speed by the AA (`speedRefs` 230 / 240), not a drone's 12 cells/s | the drone's reference | `b35d3b6` |
| 21 | AA vs jets: Igla 35% at best, a hit takes 60% (a hurt jet limps home); air defence vehicle 70%, a hit kills | other numbers | `b35d3b6` |
| 22 | D2, D3, D4: patrol 60 / 70 m (strikes 30 / 35, floor 10), cruise 20 / 21 cells/s (dash 28 / 30), fuel 3.5 / 4 min | realistic heights, faster, longer | `b7c7e80` (E8) |
| 23 | D1: jet scales 1.0 and 0.9 (span and length in content) | real size | `b7c7e80` |
| 24 | D7: the fighter's signature 50 / 200 / 60 | other numbers | `b7c7e80` |
| 25 | D9: the airfield facility builds the jets into a free station (an open stand first), held until the first order; facility 1 bay, 2 orders; a finished jet with no free station waits in the facility | a hangar piece | `b7c7e80` |
| 26 | D18: two multirole presets in the Sentinel lobby ("Multirole 500 lb", "Multirole 2000 lb") | one loadout | `b7c7e80` |
| 27 | The Overseer has no `airVision` (it does not cue AA; it would also add radar fog spotting) | give it one (content) | `b7c7e80` |
| 28 | Fighter sustained turn 20 cells at cruise (the plan's 14 is impossible: max-g 15.5), orbit 22, edge margin 55 | — | `b7c7e80` |
| 29 | New armour class `aircraft` for jets in the air; `air_gun` medium = light (60) | — | `b7c7e80` |
| 30 | Rearm a round in 5 s (multirole) / 3.5 s (fighter): a full Rearm at a loading shelter about 35 s; per-weapon rearm times later | per weapon | `b7c7e80` |
| 31 | One reload serves all an aircraft's air weapons (a release a second); the gun before missiles | per weapon | `03ec89c` (E6) |
| 32 | A missile from a jet flies at its own speed (the jet's is not added) | add the launch speed | `03ec89c` |
| 33 | It fires the shortest-ranged missile that reaches (IR close, radar far) | the best kill chance | `03ec89c` |
| 34 | Guard's air hunt looks again twice a second (staggered), keeps its target between | every tick | `03ec89c` |
| 35 | Engaging, it flies at the target's height; closing for the gun at the target's speed plus 3 cells/s within four gun ranges | — | `03ec89c` |
| 36 | A break once per missile; at most `perMissile` salvos a missile; evasion and countermeasures also while homing | — | `03ec89c` |
| 37 | The FPV drone flies 8 m up as an air target | — | `03ec89c` |
| 38 | `KilledBy` is `Blast` for any death not by gun, missile, fuel or crash (including on the ground) | — | `03ec89c` |
| 39 | Bomb gravity 70 m/s^2 (real g with the compressed speeds would release 80 cells out); wreck gravity 40 | real g | `59f5e85` (E7), `a3bc2a3` |
| 40 | The IP distance (16 cells) is measured from the release point, not the target | from the target | `59f5e85` |
| 41 | Dive roll-in height = release height + IP distance x tan(dive) (82 m for the multirole); dives release only descending | — | `59f5e85` |
| 42 | Strafing opens 18 cells out, a burst every 5 ticks, 1-cell splash (one burst a second at 9 cells gave one a pass); dive line 12 degrees, walk +-2 cells, jitter 0.5 (code constants) | other numbers | `59f5e85` |
| 43 | Egress 2 s with a 60-degree turn toward the map's centre; run-in needs the aim within 45 degrees; guided release within 30 (code constants) | content | `59f5e85` |
| 44 | An unseen strike target: it attacks the last seen position for 10 s | abort at once | `59f5e85` |
| 45 | A point strike bombs, then strafes the point | bombs only | `59f5e85` |
| 46 | Guard: an air target being engaged comes before a ground pick | — | `59f5e85` |
| 47 | D5: the technical's heavy MG engages aircraft below 40 m (`crossHalf` 3 rad/s, `bullet` 100% vs `aircraft`): ejections happen today | only jets' guns | `a3bc2a3` (E10) |
| 48 | D8: the pilot is not a unit while he comes down (untargetable); his unit appears on landing with a new ID | targetable | `a3bc2a3` |
| 49 | D9b: a wreck's impact 150 damage over 3 cells; a small crater on a runway | visual only | `a3bc2a3` |
| 50 | The wreck is its own list (not a projectile); drag 10% a second, break-up 40% on missile kills (50% drag); drones have no `loss` block | — | `a3bc2a3` |
| 51 | One descent rate 6 m/s after the rise; no map wind | a wind vector per map | `a3bc2a3` |
| 52 | The pilot lands within 16 cells (code constant); no drowning (no water in the sim) | — | `a3bc2a3` |
| 53 | D17: both jets fly the overhead break; drones stay straight-in | straight-in for jets | `ad29663` (E9) |
| 54 | `corridorNeed` 60 cells for both jets (the plan's 55 for the multirole is shorter than its initial point plus join turn) | — | `ad29663` |
| 55 | Placement keeps the type's 30-cell corridor; each aircraft checks its own need at home choice, with displaced thresholds; a field that never fits is no home and does not build that jet | refuse placement | `ad29663` |
| 56 | Pattern radius 5/4 of the tightest turn at break and approach speeds; join circle 5/4 at cruise; IP 40 cells, break 14 cells/s, pattern 40 m, fix 12 cells / 12 m; military takeoff acceleration 1.2 cells/s^2 | other numbers (content) | `ad29663` |
| 57 | Drones get no `corridorNeed` number: their straight-in join is checked from their flight (`JoinBand`: approach turn radius plus a second; Watcher about 79 cells beyond the approach end) | a content number per drone | `9547c33` (E11) |
| 58 | The air defence vehicle keeps its drone-era reach against jets (radar 260 cells, missile 220): on a 256-cell map it covers nearly everything, which is why the jets demo had to script it. Proposals, not built: a per-class maximum range on interceptors (say 100 to 120 cells against jets), a radar horizon (low jets masked by terrain, the plan's case for low ingress), a lower `airVision` against aircraft classes, or more chaff | keep as is | `b27ab47` (E11: documented, no number changed) |
| 59 | An uncrewed vehicle's radar still counts as a sensor (as an empty vehicle's eyes still see) | radar needs a crew | `b27ab47` (E11: noted) |
| 60 | D-FZ1: the fly zone is the map grown by `flight.flyZoneMargin`, 48 cells by default, at least twice the turn radius plus a second of travel (so the jets' defaults would be 48.4 and 52 cells; content sets 50 and 55); per aircraft, `World.FlyZoneMargin` the widest for renderers | one zone for all aircraft; other margins | E12 |
| 61 | D-FZ2/3: soft rule (off the map, aim behind: a full turn toward the aim), hard rule (fly zone less a turn radius plus 1 s: a full turn toward the centre); orbit centres, waypoints, areas and lanes clamped to the map only (supersedes rows 12, 13 and 16; `edgeMargin` retired) | keep a margin inside the map | E12 |
| 62 | D-FZ4: an air viewer over the band sees its own disc clipped to the map | the disc at the edge | E12 |
| 63 | Height planner numbers (code): 5 s look-ahead (jets' `lookAhead` 100), samples every 2 cells, plans climbs at 3/4 of its climb rate, drones keep 10 m over roofs (jets their `floor`), follows the ground down at half its climb rate, vertical acceleration: drones full climb in 1 s, jets half their g (full g on a strike, a break or an engagement), 1 m hold band | other numbers (content) | E12 |
| 64 | (superseded by row 70) A jet's break now dives with a bounded vertical acceleration (its g), not 0 to 40 m/s in a tick: in `AirToAirTests`' geometry the Igla no longer overshoots (kill chance 6.3% against 5.7% flying straight); the break's worth against MANPADS is now its turn | a harder push-over; a longer warn time; more flares | E12 |
| 65 | D-TURN1: a fresh player order gives a jet a quick turn (up to 2 s, idle, speed brakes at cruise less corner speed a second squared, never below corner speed, over within 10 degrees); sustained turns tightened: multirole 16.2 cells (was 18), fighter 16 (was 20): 180 degrees in 1.6 s instead of 1.95 | other numbers | E12 |
| 66 | The airfield home check still keeps the pattern on the map (not the fly zone) | allow the pattern over the band | E12 |
| 67 | (superseded by row 69) "Who is following" (pursuit separation) left for E13: measured, a gun-only fighter chasing a Watcher flies through it (closest 0.4 cells between centres); a pull-up conflicts with the 9-cell gun range and 10-degree cone (tried: closest-approach pull-up, holding a shooting place behind; both stopped the gun kills) | a longer gun range; lag pursuit; a pull-up only after the shot | E12 (not built) |
| 68 | D-RUN1: every strike is the state machine Ingress, Align, Run, Break, Extend, Reposition, Egress; jets align 3 s (69 / 72 cells), break 60 degrees and 2 s, extend 4 s and until a run line plus a turn diameter out or the fly zone's band; same break side for the whole strike (a figure of eight through the target); run lines spread up to 10 degrees by ID, extends by (ID mod 3) half seconds; dives follow their line a push-over ahead and release only sinking at half the dive's slope; a toss runs in at 3 x its floor | other numbers (content `attackRun`); break toward the map's centre every pass | E13 |
| 69 | D-PUR1: pursuit separation: break off when the closest approach in the next 2 s would come within 6 cells (3D), 10 m above, re-attack 30 cells out (at most 6 s); pure pursuit in the last second before gun range; speed brakes closing for the gun; jet guns 15 cells (were 9) | a wider cone; lag pursuit | E13 |
| 70 | D-BRK1: breaking against a missile, a jet dispenses at its launch warning and the salvo leaves `breakPk` (flares 30%, chaff 35%) instead of 55% / 60% | a harder push-over; turning into the missile (head-on aspect) | E13 |
| 71 | D-EL1: elements of at most 4 of one kind from one command, lowest ID leads; slots 21 / 23 cells back and 8 aside (the third 42 / 46 in trail), off the lead's path; strike stagger 3 s a slot; a wingman on Guard takes its lead's pick | body-frame slots; elements across kinds | E13 |
| 72 | D-ORD1: no attack order dropped silently: `World.OrderRefusals` with a reason; an Attack at an unseen target flies to its last known cell and strikes once seen (10 s there unseen: TargetLost); a player's order clears Guard's pick; the unseen count of a strike runs only near the last seen place | refuse unseen targets with NotVisible | E13 |
| 74 | D-ORD2: a parked jet given an attack it has no weapons for rearms at its station first (implicit Rearm), then launches; attacks from the ground wait for a full tank; an airborne jet without weapons refuses with NoAmmo | refuse it too | E13 |
| 75 | D-FUEL1: jets' tank 100% up to 512 cells, 200% at 1,024 (was asked ~2.5x; 200% with D-SPD1 still holds the round trip plus two minutes); Bingo refusal when the trip (there, 20 s, home, reserves) does not fit | 250% | E13 |
| 76 | D-SPD1: jets' cruise and attack 135%, dash 120% at 1,024 cells (linear from 512); runs and strike turns at the content's attack speed with speed brakes; fly zone at least 2R + 1 s at the map's cruise | scale the dash fully | E13 |
| 77 | D-ALT1b: a sensor in the air (a jet's radar) measures its air vision as a level distance, not slant: at D-ALT1's heights (up to 168 cells up on a 1,024-cell map) slant left a fighter's 160-cell radar blind to low drones (the bigsky smoke test's guarding fighter fired at nothing); ground sensors keep the slant; missile and gun ranges stay slant | scale air vision with height | E13 |
| 73 | D-ALT1: jets cruise 140 m (fighter 168) up to 512-cell maps, 280 m (336) at 1,024, at most 320 (384); ingress 70 / 80 m; climb-out to the ingress height; MANPADS reach cruising jets far less (see "Flight heights by map size") | other heights (content `heightByMap`) | E13 |
| 78 | D-AB1: the core is one site on one bar (120 s); finished, it lays its pieces finished on their slots (no passive sub-sites like the first row's runway); the core stays as invulnerable pavement under the base | sub-sites growing with its bar | H1 |
| 79 | D-AB2: the command building is the base's facility (its standing gates growth, production and the loading and heavy service); rebuilt on its slot when gone (60 s) | the core as the facility | H1 |
| 80 | D-AB3: command down: every station refuels; shelters and heavy bays stop rearm **and repair**; open stands keep their 1x (the forward rule) | stands stop too; repair goes on | H1 |
| 81 | D-AB4: the hangar: 6,000 HP, rubble 50%, repair half its build time; rubble closes all 4 bays, parked heavies take 40% and wait; gone, rebuilt on its slot (90 s) with its bays | heavies relocate | H1 |
| 82 | D-AB5: production on the hangar (tags jet, drone, aircraft), 2 at a time, 10 orders (the garage's), only while the command building stands (paused, queue kept) | 1 at a time (the facility's) | H1 |
| 83 | D-AB6: a heavy is queued only while free heavy bays exceed the heavies queued or building at the hangar; jets are not limited (they wait in the hangar as before) | limit every aircraft by stations | H1 |
| 84 | D-AB7: heavy bays: class L to XL only (content `minClass`), rearm and repair 2x, refuel 1x; nose in, out by pushback to an apron node 6 cells out | 1x like a stand | H1 |
| 85 | D-AB8: shelters and stands take S and M only (`maxClass` M: the A-10 type's 17.5 m span would fit a shelter) | span limits only | H1 |
| 86 | D-AB9: one column of rows at (148, 34), cap 12 rows (8 extra); rows attach on the core's own pavement (the attach point y 74 to 86 lies inside the core's footprint in the layout) | more columns | H1 |
| 87 | D-AB10: runway 15 / 7 / 10 cells, a connector every 4th segment, extension segments 20 s; wear patch 5 cells (it must divide the 15-cell width), 24 craters; holding 40 out, 30 round, 60 m + 20 m, 6 places; taxi wait 15 s | other numbers (content) | H1 |
| 88 | D-AB11: linking: a forward field within 40 cells of the apron band joins the base's taxi graph one way (the base's aircraft may park at its stations, over a straight link edge between the closest taxiway nodes, and use the base's runway); the forward field's own aircraft keep to their field; service and homes shared by every own field (as before) | a two-way join; a drawn link taxiway piece | H1 |
| 89 | D-AB12: the radar's ground reach spots vehicles (and grounded aircraft) anywhere in range, ridges and woods ignored, soldiers only on forest-free cells; not structures, not hidden units; a level distance from its centre | line of sight for ground radar | H1 |
| 90 | D-AB13: the radar's air reach is its `airVision` from its top (8 m), slant, signature-scaled; a radar collects the side's cues with no AA about | from a soldier's eye | H1 |
| 91 | D-AB14: the fog-clear radius is the ordinary structure vision (line of sight from its top, counted from the footprint's edge: +6 cells from the centre) | from the centre (content 540) | H1 |
| 92 | D-AB15: rubble sees nothing (no fog viewer, air sensor or ground radar) for every rubble structure but tunnel entrances: the forward facility's rubble stops seeing too | radars only | H1 |
| 93 | D-AB16: placeholders: core 5,000 HP / 6,000, command 4,000 / 2,000 (15 m), hangar 6,000 / 2,500 (20 m), radar 1,500 / 1,500, radar site 1,200 / 1,500 (8 m), heavy runway segment 250 | the owner's numbers | H1 |
| 94 | D-AB17: the core is refused where other ground units stand on its three buildings' slots (builders excepted); units that walk onto those slots while the core is a site (it is pavement) are moved to the nearest passable cell off the buildings when they appear (ID order) | refuse over the whole footprint | H1 |
| 95 | D-AJ1: the attack jet's flight derived from the multirole: cruise x 0.8 (16), attack 18, no afterburner, corner 11 / 4.5 cells (9.5 at cruise), 100 m up, ingress 40 m, floor 8 m, 6 min of fuel; 700 HP, sight 35, air vision 30 | other numbers (content) | H2 |
| 96 | D-AJ2: toughness: Igla 50% best, a hit 25% (owner's 4 hits); AD vehicle 75%, a hit 50%; the jets' missiles 80% and a hit kills; the jets' guns 55%; heavy MG 50% (12 damage: a scratch) | jets' missiles 50% too | H2 |
| 97 | D-AJ3: the bombs' release: a level run 40 m up with high-drag bombs (retard 80, about 55% of their speed a second), landing 11 to 13 cells ahead (the owner's "over the target") | a shallow dive; slick bombs | H2 |
| 98 | D-AJ4: fire mode default `single` (the owner listed it first); a salvo is centred on the aim point (2 before, 2 after), 4 cells apart | default salvo4; the stick starting at the aim | H2 |
| 99 | D-AJ5: a fire mode applies from the next pass; it is per aircraft and kept through landings (`Unit.FireMode`, `SetFireMode` with `index`) | a weapon-wide setting | H2 |
| 100 | D-AJ6: grouping: queued aims within 12 cells (group) of the pass's first, on the far side of it toward the farthest of them and at most 3 cells (groupAside) off that line, share the pass; the run starts at the end nearer the jet | a best-fit line through all | H2 |
| 101 | D-AJ7: after a pass the aims that got a bomb are done; the last aim left goes on as a plain order (E7: bombs, then the gun); a dead or lost aim moves the queue on; at most 8 aims (code constant) | end after the last aim's pass | H2 |
| 102 | D-AJ8: AttackLine: one pass in the drag's direction, the salvo spread end to end (single mode: one bomb at the middle); bombed, its order ends quietly; only free-fall bombs | all its bombs along the line; repeated passes | H2 |
| 103 | D-AJ9: a forced pass (a line, a group) flies to an entry point a run line plus a turn diameter behind its first drop (clamped to the fly zone), then turns in; on its run it follows the line | fly straight at the first drop | H2 |
| 104 | D-AJ10: a planned drop is skipped when passed by more than the release window or off the line by more than the window, the scatter and (forced) groupAside; at most one bomb a tick | abort the whole pass | H2 |
| 105 | D-AJ11: its air-to-air missile is a lighter `aam_ir_light` (30 cells; against jets 40 to 45%, a hit 60%, never a kill) and its gun's `heavy_cannon` does 40% to aircraft, so it loses to the fighter (16 of 16 duels) | the jets' `aam_ir` (it won 1 of 6 duels with it: the fighter ran dry) | H2 |
| 106 | D-AJ12: 12 flares, 2 chaff (it works low over MANPADS; with 4 chaff it outlasted the fighter's radar missiles in one duel) | more chaff | H2 |
| 107 | D-AJ13: the jets' guns hit it at 55% at best (a big slow target; the jets 25 to 30%) | 45% | H2 |
| 108 | D-AJ14: Shift on an Attack at an air target, or by an aircraft without bombs, replaces the order as before (no air queue) | queue air targets too | H2 |
| 109 | D-GS1: the gunship's flight: cruise 12 cells/s, 13 full power, no afterburner, corner 9 / 10 cells (17.8 at cruise), 360 m up on every map, climb 10 m/s; 1,600 HP; sight 50, air vision 30 | other numbers (content) | H3 |
| 110 | D-GS2: endurance 45 min, longer than the Overseer's 40 (the owner's "longest of all aircraft" read as every aircraft, drones included) | the longest of the manned aircraft only (e.g. 15 min) | H3 |
| 111 | D-GS3: toughness: every missile hit takes 13% (8 hits); AD vehicle 80% best, jets' missiles 85%, light IR 70%, Igla 60% (low only); guns 60 / 30 / 60% for 90 / 60 / 12 | 12.5% exactly (permille hit damage) | H3 |
| 112 | D-GS4: the AA soldier's ceiling is an interceptor `ceiling` over the ground under the target, 320 m for the Igla (the jets' highest cruise), so a fighter cruising above 320 m on 1,024-cell maps is also out of its reach (it was only within its slant reach from a hill) | the orbit height alone (no ceiling) | H3 |
| 113 | D-GS5: the orbit order is Attack-ground (a cell) or Attack (a ground unit: the area follows it; dead, it works the area on as attack-ground) | a new command kind; ending the order when the unit dies | H3 |
| 114 | D-GS6: orbit radius 30 cells, area 15 cells, on the circle within 4 cells and 30 degrees of the left wing; the steering chases a point 30 degrees ahead on radius / cos 30 | other numbers (content) | H3 |
| 115 | D-GS7: out of every round it goes home on its own as a recall (refuel, held); bingo ends the order (E2's refuel trip) | rearm and come back on its own; resume the orbit after the refuel | H3 |
| 116 | D-GS8: each cannon picks by its own priority of armour classes (light: soldiers, light and medium vehicles, parked aircraft; heavy: vehicles, structures, parked aircraft, soldiers; very big: structures and armour, the most max HP); the ordered unit first when its class is listed | every cannon at the ordered unit | H3 |
| 117 | D-GS9: with nothing seen in the area, the light and heavy cannons fire at seeded points of it on an attack-ground order (area fire, unscattered); never on an attack order; the very big round never | hold all fire without a target | H3 |
| 118 | D-GS10: the very big round is fired from the side cannon like the others (a straight shot at 10 cells/s), automatically at the biggest structure or armoured vehicle seen; no command for it | a dropped bomb; a fire command | H3 |
| 119 | D-GS11: fire cycles: light a round every 0.4 s (40 s of fire), heavy every 2.5 s (50 s), the big one at once; each cannon on its own reload; rounds at 20 / 15 / 10 cells/s | bursts; one reload for all | H3 |
| 120 | D-GS12: scatter per axis 3 / 2.5 / 2 cells for aimed rounds, led by the flight time; the heavy round looks like the 500 lb (tier 4) at half its damage | other numbers (content) | H3 |
| 121 | D-GS13: 12 flare salvos at 35% (a jet's 55%: the angel wings decoy better), one a missile, no chaff, no break | perMissile 2; chaff | H3 |
| 122 | D-GS14: class XL; 90 cells to land, 80 to take off, the overhead break; service 45 s tank, 0.5 s a round, 25 HP/s; build 90 s, 4,000 (placeholders); no ejection on a loss | a straight-in pattern; a crew bail-out | H3 |
| 123 | D-GS15: the Watcher and the Overseer get buildTime 60 and 45 s (their stations' replacement times) for the hangar; their cost stays 0 (free from the hangar: a follow-up for the owner) | their stations' replacement supply costs (800 / 600) as cost | H3 |
| 124 | D-GS16: `World.RadarTracks` lists enemy air units only (flying aircraft and air-body units, not one-way drones, which are projectiles), detected by the radar's own sensor (not the side's other cues) | include projectiles; include the side's cues | H3 |
| 125 | D-GS17: gunships ignore element slots on the orbit; sharing an area they space evenly round the circle by slowing only (the one with the widest gap ahead sets the pace) | a radius offset per gunship; element slots | H3 |
| 126 | D-SB1: the bomber's flight: cruise 14 cells/s, 15 full power, no afterburner, corner 10 / 12 cells (23.5 at cruise), climb 10 m/s, 400 m up on every map (its runs too); 2,000 HP; sight 40, air vision 30 | 360 to 450 m (at 440 m the AD vehicle's 220-cell missile no longer reaches it at all) | H4 |
| 127 | D-SB2: fuel 30 min at cruise (the jets 3.5 to 6, the gunship 45: the owner's longest stays the gunship); no transit speed-up on big maps | 20 min; the attack jet's 125% transit speed | H4 |
| 128 | D-SB3: toughness: every missile hit takes 17% (6 hits: 5 leave 300 HP); AD vehicle 85% best, jets' missiles 85%, light IR 70%, Igla 60% (low only); guns 60 / 30 / 60% for 90 / 150 / 12 | 16.7% exactly (permille hit damage) | H4 |
| 129 | D-SB4: `strategic_bombs`: 2,000 damage (tier 7), rings 5 / 13 cells, large crater, slick, level from 400 m, scatter 1.5 cells | 1,800 (the tier's floor); retarded bombs | H4 |
| 130 | D-SB5: carpet20 is the default (the owner's "all on target"), 2.5 cells apart (47.5 cells), centred on the aim | 3 cells (57); the carpet starting at the aim | H4 |
| 131 | D-SB6: a fire mode's `spread` (carpet20): one stick for the pass, end to end on a line, first to last aim on a group, at least `stickSpacing` apart (a shorter line or group stretched about its middle); salvo4 keeps the attack jet's stick at each aim | squeeze 20 into a short line (some skipped: one bomb a tick); 20 at each grouped aim | H4 |
| 132 | D-SB7: Shift grouping reach 48 cells (the carpet's length); with carpet20 far aims after the first get nothing (out of bombs) | split the 20 between the queued aims | H4 |
| 133 | D-SB8: `overfly`: after its last drop it flies on down the run line until past the last drop, then breaks (an unforced overfly pass follows the line carrot on its run too); the jets keep breaking at release | break at release (the jets' rule) | H4 |
| 134 | D-SB9: a long bomb run: align 80 cells (5.3 s), IP 15 cells, break 30, extend 60 (with a 30-cell align the 50-cell fall put drops 4 to 5 cells aside and they were skipped) | a wider release tolerance | H4 |
| 135 | D-SB10: flares 30 salvos, two a missile at 55% each (about 30% left: the jets' break-and-flares cut), no break, no chaff | the jets' break (it would leave its run and dive toward the AA ceiling); one salvo at 35% | H4 |
| 136 | D-SB11: no `evade` and no air weapons; Guard default vehicles and structures | infantry too | H4 |
| 137 | D-SB12: class XL at 0.82 scale (span 46 m, length 40 m) so it fits the heavy bay's 48 m door; 100 / 90 cells to land / take off; service 60 s tank, 3 s a bomb, 25 HP/s; build 120 s, 5,000 (placeholders); crash 400 over 6 cells, no ejection | real scale (56 m span: a wider bay door) | H4 |
| 138 | D-SB13: fly zone 65 cells (the loader's rule for its turn), now the widest, so `World.FlyZoneMargin` grows from 55 to 65 cells (the client draws ground that far out) | a tighter turn to stay at 55 | H4 |
| 139 | D-SB14: elements: the default slots (1.5 turns back, half a turn aside); no element tuning for bombers yet | a trail cell with its own slots | H4 |
| 140 | D-SB15: D-112 kept: the Igla ceiling cuts only a fighter cruising on 1,024+ cell maps, and only from ground 56 m+ above it (its 280 m slant keeps it out over level ground anyway); no jet change | a ceiling over the shooter's ground; 340 m | H4 |
| 141 | D-FIX1: a gunship out of rounds goes home on its own **to rearm** (the full service in its heavy bay: 100/20/1 in about 30 s), then is held parked until its next order; was a recall (refuel only, held). The hold was lost anyway: the scout system joined the airfield a second time in the same tick (a fresh `AirOps`), so it pushed back out with 0/121. `noResupply` was not the cause (it only keeps field resupply off) | a recall that stays held, rearmed by the next attack order (D-ORD2); an automatic sortie back to the area once rearmed | fix |
| 142 | D-FIX2: the heavies (attack jet, gunship, bomber) taxi 5.5 cells/s, 3.5 in turns, and push back at 3 (were 3/2.5, 1.5/1.2 and 0.5): order to airborne from a heavy bay 39.4 / 42.0 / 42.5 s (were 89.0 / 98.3 / 98.8 s). Content only; the jets at forward airfields keep theirs; taxi stays below every touchdown speed | a shorter pushback path (the bay's `parkOut`); a nose-out bay | fix |
| 143 | D-FIX3: a planned pass (line, queue, stick or carpet) that ends the strike with **every** weapon stock empty sends the aircraft home on its own to rearm, then held parked (the gunship's D-FIX1 rule); the bomber after any carpet, the attack jet only when its gun and missiles are gone too (anything left: it orbits on, E2). Code only: no golden change (1126/1126) | home when its bombs are gone even with the gun or missiles left; keep E2 (orbit, the player rearms) | polish |
| 144 | D-LAND1: landing at the main air base is quicker: every aircraft taxis there at 5.5 cells/s, 3.5 in turns (floors: the heavies' speeds; a jet's own 3.5 / 1.5 still at a forward airfield) and the overhead break is 30 cells in from the threshold (was midfield, 90 cells in). Final 38.9 / 36.9 / 55.4 / 54.1 s -> 25.4 / 25.6 / 39.4 / 39.5 s (attack jet, multirole, gunship, bomber); multirole touchdown to parked at a stand 65.8 -> 40.5 s (rollout 20.3 -> 14.0, taxi and park 45.5 -> 26.5); the heavies' 26.8 to 27.8 s unchanged. Content (3 new type fields); golden re-recorded for the content hash only | a high-speed exit near x 130 (art and content) to bring the multirole under 30 s on the ground; taxi 7 cells/s at the base; a shorter heavy pattern | land |
| 145 | D-LAND2: an aircraft the hangar (or a facility) builds is recorded as rolled out into its station at that tick (`AirOps.Since`), parked and held; the client opens a heavy bay's door from that instead of its own 8 s hold on a new occupant. No roll-out path (it still appears at its stop mark) | a real roll-out from inside the hangar (taxi from the hangar's back wall) | land |
| 146 | D-B1-1: `supersonic_bomber` (Sentinel), class XL (main air base, heavy bay), 0.82 scale (34 m spread, 36.5 m long); build 100 s, 4,500 (placeholders) | the strategic bomber's 120 s / 5,000 | B1 |
| 147 | D-B1-2: cruise 18 cells/s, run (attack) 22, dash 24 with afterburners, engines 4 cells/s^2 (with 2, a run straight after a quick turn was flown at 15 to 19 cells/s; with 4, 16 to 21), corner 12 / 7 cells (15.7 at cruise) | engines 3; attack 23 (released nearer 22.5) | B1 |
| 148 | D-B1-3: 400 m on every map and its runs (the strategic bomber's); 25 min of fuel; 1,700 HP; the `bomber` air class (6 missile hits); sight 40, air vision 30 | 30 min; 2,000 HP | B1 |
| 149 | D-B1-4: `supersonic_bombs` 1,400 (tier 6), rings 4 / 10 cells, large runway crater, slick, scatter 1 cell per axis | scatter 0.75 (straighter rows) or 1.5 (the strategic bomb's) | B1 |
| 150 | D-B1-5: rows are a fire mode's `across` / `acrossSpacing` (generic, any free-fall bomb); the row's place stays on the run line and each bomb is pushed across by its own release velocity (not released off the line); the whole row leaves in one tick | release the row over 4 ticks (staggered along the path) | B1 |
| 151 | D-B1-6: `row4` the default, 3 cells across (9 wide); `block24` rows 3 cells apart (`stickSpacing`): 9 x 15 cells, worst point 2.5 cells from an impact | 2.5 cells (the strategic bomber's spacing: denser, 7.5 x 12.5) | B1 |
| 152 | D-B1-7: Shift grouping reach 30 cells (twice the block's length), aside 3 cells (the strategic bomber's 48 / 3) | 15 cells (the block's length); a wider aside for rows | B1 |
| 153 | D-B1-8: row4 on a dragged line lays one row at its middle (its one place, as `single`); block24 spreads its 6 rows end to end | row4 on a line: rows spread along it (block-like) | B1 |
| 154 | D-B1-9: its bomb run: align 100 cells (4.5 s at 22: a 75-cell fall), break 30, extend 70, release window 2 cells | a shorter align with a wider window | B1 |
| 155 | D-B1-10: flares 30 salvos two a missile, no break, no chaff; no air weapons; Guard default vehicles and structures (the strategic bomber's) | a break with its afterburner | B1 |
| 156 | D-B1-11: service 50 s tank, 2.5 s a bomb (24: 30 s in a heavy bay), 25 HP/s; 100 / 90 cells to land / take off; crash 350 over 5.5 cells, no ejection | a crew bail-out (the B-1 has ejection seats) | B1 |
| 157 | D-B1-12: fly zone 50 cells (twice its turn at cruise plus a second); `World.FlyZoneMargin` stays 65 (the strategic bomber's) | — | B1 |
| 158 | K-1: the Kaman is a flying unit (a drone flight, air class `kaman` appended last), not a projectile: air target, radar track, vision, wreck for free | a projectile drone with its own vision and HP rules | kaman |
| 159 | K-2: "spotted by its side" is the player's view (`IsVisibleTo`): without fog the target is never lost while it lives in the open; with fog it is lost when nobody of his side, the drone included, spots it | lost by spotting even without fog | kaman |
| 160 | K-3: a lost target seen again before the blast is taken up again; a dead one is never (its last position is dived on) | lost for good once lost | kaman |
| 161 | K-4: 150 m over the ground (terrain following), 15 cells/s, climb 12 m/s, turn 60 degrees a second (the default 48-cell fly zone needs at least about 52); 100 s of fuel (1,500 cells) | 30 m over the highest ground like the one-way drone | kaman |
| 162 | K-5: launch range 60 to 1,200 cells; beyond it the launcher drives closer, as any launcher | a longer range with the fuel | kaman |
| 163 | K-6: missiles 126 `atgm` (the Kornet's), fire and forget 40 cells/s, 45 cells guided, within 30 degrees of the nose; from 30 cells level distance, 1 s apart; they come off in the dive | a heavier missile (the helicopters' 500) | kaman |
| 164 | K-7: the dive at 45 degrees with a 2 s pitch-over, starting about 90 cells out from 150 m; in the dive the target, else the first unit or roof, sets it off | a steeper (60 degree) dive starting nearer | kaman |
| 165 | K-8: blast 520, 6 cells, 100% to 40%, `kaman_charge` armour 250%, buildings 45% of max HP (`maxHpPercent`), a large runway crater | a two-ring splash (full within 2 cells) | kaman |
| 166 | K-9: 200 HP; Igla 70% best, a hit 50%; air defence vehicle 90%, a kill; Patriot 50%; jets' missiles 90%, guns 70 / 40; the technical's heavy MG does not engage it | Igla speed reference 15 cells/s (no 0.8 speed penalty) | kaman |
| 167 | K-10 (**confirmed by the owner, 2026-10-22**): helicopters cannot shoot it: they have no air-to-air weapon in the engine yet (the owner's answer listed them); `kaman` goes in such a weapon's classes when one comes | a Stinger-type pair on the attack helicopter (new heli air-to-air) | kaman |
| 168 | K-11: the truck: medium 6x6, 220 HP, speed 2.2, 1,800, 40 s, deploy / stow 2 s, 1 Kaman, reload stowed at a depot 60 s / 400 | — | kaman |
| 169 | K-12 (**confirmed by the owner, 2026-10-22**): the engineer's Kaman option is a second kit (`kaman_station_kit`, own charge, 3 s, refilled at a depot 30 s / 400), so he may hold a drone station and a Kaman station; a unit reloads one depot-refilled stock at a time | one shared kit charge, picking the drone at set-up | kaman |
| 170 | K-13 (**confirmed by the owner, 2026-10-22**): the Kaman station holds fire (only its owner's orders launch it); 80 HP, hidden, gone 20 s after it is spent | the drone station's Guard default | kaman |
| 171 | K-14 (**confirmed by the owner, 2026-10-22**): shot down, its wreck falls (`loss`: 100 crash damage over 2 cells); its warhead does not go off | the full blast where the wreck lands | kaman |
| 172 | K-15: it takes no orders once launched (no retarget, no abort) | a retarget order while it cruises | kaman |
| 173 | MR-1: the mobile radar: light tracked, `medium_vehicle`, 280 HP, speed 30 (the air defence vehicle's), 6 cells of sight, no weapon, a driver; 40 s and 1,600 at the vehicle garage | wheeled; speed 25 | mobile-radar |
| 174 | MR-2: set up 8 s, packs up 6 s; it searches only set up; it sets up on a Deploy order only (as the radar truck), not by itself when it stops | auto set-up after a few idle seconds | mobile-radar |
| 175 | MR-3: air search 400 cells, slant from its antenna 7 m up (`height` 70), by signature (the fighter 200); air units only | 360 like the radar truck; 500 | mobile-radar |
| 176 | MR-4: the radar-cued lock is a generic archetype field `cueLock` (70%): any ground interceptor of its side whose current target a set-up cueing radar tracks; after the signature; truncated ticks (30 -> 21, 20 -> 14); the lowest of several radars; the jets are not cued | 75% or 60%; cue the jets too | mobile-radar |
| 177 | MR-5: the cue shortens the lock only: the kill chance and the range are the shooter's own (the tracking bonus counts from the shortened lock) | a kill bonus for cued shots | mobile-radar |
| 178 | MR-6: the Sentinel radar truck, the radar site and the air base's radar keep no cued lock (`cueLock` 0) | give them one | mobile-radar |
| 179 | MR-7: no ground radar; it is not a radar for radar-linked launchers (the Patriot type: a radar is a unit with a `groundRadar`) | ground reach 80 like the radar truck's | mobile-radar |
| 180 | MR-8: no emitter rule: the enemy sees it like any vehicle | set up, it is spotted from farther (an anti-radiation hook) | mobile-radar |
| 181 | MR-9: `World.RadarTracks` is now empty for a unit that sets up while it is packed up (the radar truck too: a client query; its sensing was already set-up only) | — | mobile-radar |
| 182 | MR-10: the antenna sweep (`sweepTicks` 80: one turn in 4 s) is a pure function of the tick and the unit's ID, not state; it restarts at another bearing after each set-up | a hashed set-up tick | mobile-radar |
| 183 | SA-1: the SA-6 launcher (`kub_launcher`): tracked, `medium_vehicle` armour (the radar's chassis), 280 HP (owner: the Osa's), speed 30 (the radar's), 6 cells of sight, a turret, a driver; 45 s and 2,000 (1,800 + 200 missiles) at the vehicle garage | `light_vehicle` like the Osa | sa6 |
| 184 | SA-2: a linked radar is any working unit of its side with an air vision and the tag `radar` (the mobile radar; the Sentinel radar truck carries it too, but no player has both) within 60 cells (level) of the launcher; it must track the target itself (its own reach by signature), its side's team cueing is not enough; without one it holds fire | any track of its side's within the link; a dedicated radar tag | sa6 |
| 185 | SA-3: semi-active: in flight a radar linked to the launch point must track the target every tick; once lost, lost for good (no reacquisition); the missile flies on straight (no gravity drop) to its max flight and blows itself up; the launcher's own death does not matter | reacquire within a grace time; tie the link to the living launcher | sa6 |
| 186 | SA-4: 350 cells of slant (owner: about 1.6x the Osa's 220), minimum 15, 55 cells/s, guided for 480 cells, turn 60 degrees a second, lead 100 ticks, launched 3 m up | 400 cells; 60 cells/s | sa6 |
| 187 | SA-5: damage (owner: about 1.3x the Osa's): `killBest` x1.3 capped at its `killMax` 95 (91 jets and attack / armoured helicopters; 95 attacker, gunship, bomber, light helicopter, Kaman; 26 scout, 52 Watcher), `hitDamage` x1.3 (attacker 65, gunship 17, bomber 22, attack helicopter 44, armoured helicopter 34; a light helicopter dies), the Overseer 85% a hit; the other kill terms the Osa's | no cap (a near-certain kill); a higher `killMax` | sa6 |
| 188 | SA-6: no height ceiling (the Osa has none either): "higher" is its slant reach (the bomber 400 m up within about 287 cells of ground, the Osa's 92) | a `ceiling` on both (the Osa's below the bomber's 400 m) | sa6 |
| 189 | SA-7: it engages the Osa's air classes but the FPV drone (low, cheap: not worth one of 3 missiles); one-way drones yes | the FPV too; aircraft only | sa6 |
| 190 | SA-8: lock 1 s (the Osa's; the mobile radar cues it to 0.7 s), one missile every 2 s, one at a target at a time | a longer lock for the illumination | sa6 |
| 191 | SA-9: sets up 5 s and packs up 4 s on Deploy / Stow orders only (as the Patriot type); fires only set up | sets up by itself when it stops | sa6 |
| 192 | SA-10: reload only at a depot, packed up, 1 missile every 20 s for 200, 1 s after the last shot; set up, idle and short at a depot it packs up by itself (the launchers' rule), not while it tracks an air target (a `SupplySystem` change that touches only it); it does not set up again after the reload | a transloader vehicle (owner: later); set up again after the reload | sa6 |
| 193 | SA-11: chaff (not flares) answers it, like the Osa's radar missile; a lost missile is reported in `World.BatteryLost` and ends as a self-destruct | — | sa6 |
| 194 | B2-1: the owner's 700 m and "a quarter of the range" together make it **never detected in flight**: every sensor's quarter (mobile radar 100 cells of slant, air defence vehicle 65, fighter 40, the main base's radar 100) is under its 350 cells of height, and no ground weapon reaches 700 m anyway; kept as the owner's answers | detect 90+ (the mobile radar overhead), or a lower height (400 m with detect 25 is still unseen: 200 cells up), or a "seen while its bays are open" rule | b2 |
| 195 | B2-2: so it is seen and shot only low (climbing out, the approach near its base), where its fragility (3 missile hits) and few flares count | a sensor rule by ground distance for stealth; the SA-6's own reach | b2 |
| 196 | B2-3: a new `stealth` air class (appended) with every interceptor's `bomber` numbers and a hit of 34% (3 hits; the missile defence 100%) | the bomber class with a hit rule of its own | b2 |
| 197 | B2-4: 1,600 HP, 6,000, 130 s, cruise 15 / run 16 cells/s, 20-cell turn, climb 12 m/s, 30 min of fuel, fly zone 60 cells | — | b2 |
| 198 | B2-5: precision = no scatter and a steered release (the miss and the offset into the release velocity), not a guided glide; every drop due in a tick leaves in it | a guided glide bomb (E7's) | b2 |
| 199 | B2-6: a lone point's pattern: all16 a 4 x 4 grid, group4 a 2 x 2, both 2 cells apart (in the 2-cell kill zone) | all 16 on the one point | b2 |
| 200 | B2-7: queued aims: at most one aim a bomb a pass (all16 16 aims, group4 4), the salvo shared out (the first ones one more), each share a grid; reach 40 cells along and 12 aside (the precision push) | one bomb an aim, the rest kept | b2 |
| 201 | B2-8: precision bomb 1,600, 100% in 2 cells, 30% to 4, tier 7 (a content `tier` overrides the damage's tier 6) | — | b2 |
| 202 | B2-9: the bunker buster: 6,000 within 3 cells (new warhead: 100% vs all armour, 200% buildings) plus the penetrator: everything there destroyed whatever its HP, occupants die in their building or vehicle | only the 6,000 (armour and HP still count) | b2 |
| 203 | B2-10: tunnel kills: everyone at an entrance whose footprint the 3 cells reach, and anyone on a passage within the 3 cells; any owner's tunnels (friendly fire as every blast) | only the entrance's own node | b2 |
| 204 | B2-11: the entrance collapses into rubble (closed, still a junction); a second hit on the rubble removes it (the existing two-stage rule) | removed at once | b2 |
| 205 | B2-12: `both` on a lone point: side by side 3 cells across (one tick); one each on two queued aims; `single` one a pass; it falls 5 s (gravity 55, drag 10%/s) | both on the same spot | b2 |
| 206 | B2-13: the deep crater: 1.5 cells, 5 minutes, visual only (no blocked cells) | a cell blocked while it lasts | b2 |
| 207 | B2-14: the weapon toggle is its own command (`SetBombWeapon` 37) with a fire mode kept per weapon; only the selected weapon drops, also on Guard; never a fallback to the other | the other weapon when the selected is spent | b2 |
| 208 | B2-15: home to rearm only with both weapons spent (the generic rule) | home when the selected weapon is spent | b2 |
| 209 | B2-16: 8 flare salvos, one a missile, no break | none | b2 |
| 210 | B2-17: the exclusive group counts alive + queued + being built; the lock is set when production first finishes one; spawned units lock nothing; refusals are silent (the client reads `ExclusiveOf`) | lock on the first order; a refusal event | b2 |
| 211 | B2-18: `flight.height` and `ingressHeight` may now go to 1,000 m (were 500) | — | b2 |
| 212 | LG-1: the airlifter: class XL, 1,800 HP, 3,000, 90 s at the main base's hangar; cruise 13 cells/s at 300 m (inside the Igla's 320 m ceiling, the owner's height); 30 min of fuel; turn 20.8 cells; 0.55 scale (span 37 m, length 40 m) | 400 m like the bombers (out of the Igla's reach) | logistics |
| 213 | LG-2: a new `airlifter` air class with the bomber's engagement numbers and a hit of 25% (4 hits; the SA-6 33%, the missile defence 50%); 30 flare salvos, two a missile, no break | the gunship's class (8 hits) | logistics |
| 214 | LG-3: the cargo is a weapon (`cargo_pallets`, 4 rounds) with an `airdrop` block, flown by the strike system: the airdrop is an attack-ground pass marked `Unit.AirdropAt`; only an Airdrop order uses it | its own flight logic | logistics |
| 215 | LG-4: the drop run at 150 m (owner: inside the Igla's, the Osa's and the SA-6's reach); a low drop starts down 1.5 times its sink distance before the run, climbs back between passes | the whole ingress at 150 m | logistics |
| 216 | LG-5: the pallet's fall: gravity 4.7 m/s2 and 5% of its horizontal speed lost a tick (about 8 s from 150 m, landing about 26 cells past the release); scatter up to 2.5 cells a side; `all4` a stick 2 cells apart | a slower chute (wind drift needs a map wind) | logistics |
| 217 | LG-6: a pallet lands in the first drop zone of its side that holds the point (by ID): its supply up to the store's cap, the rest lost; outside every zone all lost; it never hurts anything and nothing it falls past stops it | a pallet that crushes what it lands on; a capturable crate (owner: later) | logistics |
| 218 | LG-7: an Airdrop on a full store is not refused (the pallets are lost) | refuse it, or keep the pallets aboard | logistics |
| 219 | LG-8: pallets load only where supply is unlimited (a main air base: a station's rearm, 15 s a container at 1x, 30 s for 4 in a heavy bay); spent, it goes home to such a field and is held there loaded | load at a forward airbase out of its stock | logistics |
| 220 | LG-9: Deliver (`CommandKind` 39) lands it at a field with a stock; one container every 7.5 s while a whole one fits (30 s for 4; the rest stays aboard); refuelled from the stock; then home to load, held there | unload part of a container; stay at the field | logistics |
| 221 | LG-10: shot down with containers aboard, their cargo is lost (an event); no wreck cargo | burning crates on the ground | logistics |
| 222 | LG-11: the field ammo point: 8 x 8 cells, 800 HP, 400, 15 s; stock 0 to 6,000; its drop zone 12 cells south of its centre, radius 6 | a bigger zone | logistics |
| 223 | LG-12: the ammo point rearms its side's ground units within 12 cells (`groundOnly`): a charge every 2 s at 20 (`roundCost`), depot-reloaded weapons at their own cost and rate; no reload bonus, no repair, no fuel | a reload bonus like a depot's (20%) | logistics |
| 224 | LG-13: the supply stash: 20 x 14 cells, 1,500 HP, 800, 20 s; storage only (`storage`: no rearm, no depot reloads); stock 0 to 15,000; its zone the west half (radius 8) | — | logistics |
| 225 | LG-14: the truck's load stays 400 (the owner: keep its cargo behaviour); loading takes its unload time (3 s); it loads only a full load and waits beside an empty store | a 2,500 container (no depot could take it: 1,000 cap); partial loads | logistics |
| 226 | LG-15: a store takes a truck's load only on an Unload order aimed at it (a full truck at a stash never unloads back); an ammo point and a depot take loads on their own as before | — | logistics |
| 227 | LG-16: trucks load free at a main air base (unlimited `airbase_supply` on its core, within 30 cells of its centre) | load at the hangar only | logistics |
| 228 | LG-17: the forward airbase is a new core-founded type (140 x 64, 30 s, 3,000): a 7-segment runway of the main base's segments (140 cells), 2 stands for any class, a 2-bay garage (the facility, a target piece), no radar, no extra rows; extension segments allowed | a bigger runway; its own segment art | logistics |
| 229 | LG-18: its stock: 8,000 when built or spawned, 20,000 at most; a round 20 (a weapon's depot cost when it has one), 100 HP 10, a full tank 200 (pro rata), a truck's load 400 | other prices | logistics |
| 230 | LG-19: a round (or flare salvo) waits for the stock; fuel and HP stop at 0; in the garage each part is paid as it starts or skipped; empty, aircraft wait parked unserviced | aircraft leave unserviced | logistics |
| 231 | LG-20: its stands rearm and repair at 2x and refuel at 1x (the heavy bay's); its garage the military garage's (reload 5 s, repair 10 s, load 10 s); its garage down: the stands refuel only | 1x stands | logistics |
| 232 | LG-21: no production: its garage's production block has 0 bays and no tags (doors only); `Produce` is ignored | — | logistics |
| 233 | LG-22: at least 120 cells from its side's main air bases (finished or sites), footprint to footprint (`awayFrom`); the other side's do not count; a scenario spawn follows the rule too | from centre to centre | logistics |
| 234 | LG-23: helicopters do not take its stands (a helicopter pad is a shelter, a stand or a helipad, never a heavy-kind station) | let them | logistics |
| 235 | LG-24: an airfield piece's factory doors, bays and rally turn with its field (the forward airbase turned a quarter) | — | logistics |
| 236 | LG-25: no automatic truck shuttle, no Shift-queued airdrops, no skirmish AI use | a looping shuttle order | logistics |
| 237 | HV-1: the Humvee replaces the technical in the Sentinel roster and lobby (2 Humvees for 2 technicals); built in either garage, as the Sentinel cargo truck | military garage only | humvee |
| 238 | HV-2: `medium_vehicle`, 400 HP, speed 40, turn rate 3, radius 8 (the technical's: 5.5 m against 5.4 m), vision 8 cells, 500, 15 s, glyph `Y`, crusher 2, tunnel slots 3 | other numbers | humvee |
| 239 | HV-3: the gun's anti-infantry edge is a warhead, `heavy_bullet` (bullet with infantry 125%), not a burst splash (a splash would hit its own riflemen and friends; the spec's simplest fit) | a 3-cell burst splash | humvee |
| 240 | HV-4: `humvee_mg` 14 at 7 cells, every 5 ticks, unlimited ammo, ground only (no `airGun`) | ammo and garage rearm | humvee |
| 241 | HV-5: 5 passengers with the `infantry` tag (workers too, like the carrier); the gunner is the vehicle's | soldiers only | humvee |
| 242 | HV-6: fast dismount = crew `unloadInterval` 8: the first at once, then one every 0.4 s (5 out in 1.6 s); the infantry carrier keeps its all-at-once unload (it had no interval: the spec's "much faster than the carrier" does not hold, the carrier was never slower) | give the carrier an interval too | humvee |
| 243 | HV-7: the unload goes on while it drives (it does not stop by itself); a second Exit does not restart it | stop for the dismount | humvee |
| 244 | HV-8: smoke as a charged ability: 2 pops, radius 6 cells, 10 s, 2 s between pops; `CommandKind.Smoke` 41 (not `UseAbility`: no target, no range) | a 45 s cooldown | humvee |
| 245 | HV-9: the screen stays where it was popped (it does not follow the vehicle) | a screen that moves with it | humvee |
| 246 | HV-10: sight lines blocked both ways: units in a screen are blind outward too; two units in the same screen see each other | inside sees out | humvee |
| 247 | HV-11: ground radars do not see into a screen (they see past one); flying units are never hidden by one | radar sees through smoke | humvee |
| 248 | HV-12: a guided missile loses guidance when its target is in a screen or (operator-guided) its operator's line crosses one; the Watcher's strike, bombs and air defence are unaffected | the Watcher's roll too | humvee |
| 249 | HV-13: charges refill wherever weapons rearm: a supply depot too (it rearms every stock), the field ammo point (20 a charge), garage service (a round from a field garage's stock) | only the garage, ammo point and forward airbase | humvee |
| 250 | HV-14: no skirmish AI use; no fire ports; no wind | — | humvee |
| 251 | MBT-1: the tank comes from the military garage only (the vehicle garage's new `production.excludeTags: ["tank"]`); not in the Sentinel lobby | either garage; one in the lobby | abrams |
| 252 | MBT-2: `armored_vehicle` 2,500 HP, speed 30, turn rate 2, radius 12 (the largest in use: the heavy trucks'), vision 8 cells, 2,500, 45 s, glyph `I`, tags vehicle, heavy (the Watcher's heavy rule), tank; no tunnel slots | other numbers | abrams |
| 253 | MBT-3: the round by target is two weapon effects of one component, the second `sharedAmmo` (one 40-round stock); the usual best-damage pick chooses; HE first, so attack-ground fires HE | a per-target round field on one weapon | abrams |
| 254 | MBT-4: the dart does 900 (the spec's "about 300 against armour" would take 21 hits head-on through 40%; its "6 to 9 head-on" needs about 900): 7 hits head-on, 4 side, 3 rear; the infantry carrier in 2, any truck or the Humvee in 1 | 300 | abrams |
| 255 | MBT-5: HE 250, a 3-cell splash 100% to 20%: a tight group of 6 riflemen dies to one round; `he_mpat` treats medium vehicles like light ones (every warhead but the ATGM does) | weaker HE | abrams |
| 256 | MBT-6: the coaxial 7.62 and the commander's .50 cal are one weapon (`tank_mgs`, 12 / 7 cells / 0.25 s, `heavy_bullet`); `coaxial`: its own reload, at the tank's current target only, the pick only when the gun cannot fire | two weapons; a commander's gun with its own target | abrams |
| 257 | MBT-7: range 20 cells (the spec's "about 200", in tenths like the Kornet's 288); the tank sees 8 cells and needs a spotter beyond | 20 cells of vision | abrams |
| 258 | MBT-8: rearm as plain ammo: a depot and the field ammo point give a round (and a Trophy or smoke charge) every 2 s at their round cost (1, 20); a garage's service all at once in 5 s | a `depotReload` with its own rate and cost | abrams |
| 259 | MBT-9: directional armour by the hull's facing only (the spec); front and rear arcs 90 degrees each at the 32-facing resolution (9 facings each, 14 side); a hit of unknown direction (a mine under it) counts as side | a wider front arc; belly hits as rear | abrams |
| 260 | MBT-10: top attack (150%) is any lobbed round (mortar, rocket and missile salvos, the ballistic missile, one-way drones), any flying shooter (aircraft, helicopters, the Watcher's and Kaman's missiles, bombs, strafing), an FPV drone's strike and a falling wreck; a splash comes from where it went off | only drones and bombs | abrams |
| 261 | MBT-11: outright kills stay: an AT mine destroys it (every vehicle), the Watcher's strike 80% of the time (`heavy`) unless Trophy stops the missile | spare tanks from mines | abrams |
| 262 | MBT-12: Trophy threats are a weapon flag (`apsThreat`): the RPG, the Kornet, the Kaman's missiles, both helicopter missiles, the Watcher's missile; never bullets, shells, bombs, artillery, rocket pods or salvos | a warhead list on the APS | abrams |
| 263 | MBT-13: Trophy does not engage drones (FPV, one-way, Kaman): the owner's open question | engage drones | abrams |
| 264 | MBT-14: Trophy engages only threats aimed at its own unit (a missile at the Humvee beside it flies past) | guard neighbours | abrams |
| 265 | MBT-15: chance 90%, -10% per 10 cells/s above 25 (rounded up), -up to 40% under 20 cells of launch distance (from the launch point to the tank when it engages), at least 10%: Kornet from afar 90, RPG from 9 cells 68, a 40 cells/s missile 70 | other numbers | abrams |
| 266 | MBT-16: intercepts 4 cells out (where the threat is when launched closer); one engagement a second (`Overwhelmed`: the second of a salvo gets through); a charge an attempt; its blast hurts nobody | a blast that hurts soldiers nearby | abrams |
| 267 | MBT-17: the roll is `Rand.Hash(seed, tick, unit, threat)` (no generator state: the world's other rolls are unchanged) | draw from the world's generator | abrams |
| 268 | MBT-18: Trophy needs no driver (an automatic system) | crewed only | abrams |
| 269 | MBT-19: the tank carries the Humvee's smoke launchers (2 pops) | none | abrams |
| 270 | MBT-20: crusher 3; `crushable` 2 on the dirt bike, the technical and the rocket jeep only (the cars), not trucks (cargo, radar, air defence), launchers, armour or the Humvee | every `light_vehicle` | abrams |
| 271 | MBT-21: against it the Resistance's Kornet takes 50 hits head-on, 25 on a side, 16 on the rear, the RPG 76 / 38 / 23 (the armour class is the carrier's, 2,500 HP): the counters are flanking, the Kaman (1,950 a dive), mines and the ballistic missile; no skirmish AI use | lower HP, or a heavy-armour class with its own warhead column | abrams |
| 272 | LT-1: the light tank comes from the military garage only (tag `tank`) or by airlift; not in the Sentinel lobby | either garage; one in the lobby | booker |
| 273 | LT-2: `armored_vehicle` 1,400 HP, speed 38, turn rate 3, radius 11 (the infantry carrier's), vision 9 cells (the spec's "90" in tenths, as the Abrams' 80), 1,500, 30 s, glyph `l`, tags vehicle, heavy, tank, airliftable; no tunnel slots | other numbers | booker |
| 274 | LT-3: the `heavy` tag (the Abrams' and the carrier's): the Watcher's strike destroys it 80% of the time, else leaves it at 25% | no tag: destroyed outright | booker |
| 275 | LT-4: the dart 600 (the spec's "200, two thirds of the Abrams' 300", scaled as the Abrams' landed 900): on the Abrams 11 head-on, 6 on a side, 4 from behind; the spec's 30 / 10-13 / 8 assumed a 300 dart, landed every count is a third (the Abrams kills it in 3 head-on), so its ratios hold | dart 210 (the spec's literal counts: 30 / 15 / 10, while the Abrams needs 3) | booker |
| 276 | LT-5: HE 170 (two thirds of 250), a 2.5-cell splash 100% to 20%; no runway crater from HE (under 250), a small one from the dart | other numbers | booker |
| 277 | LT-6: 18 cells (the Abrams 20), 6 s, on the move, 35 rounds shared; the MGs are the Abrams' `tank_mgs` | its own MGs | booker |
| 278 | LT-7: every Resistance launcher (and the radar) dies to one dart (150 to 300 HP against 600): the spec's "1 to 2"; their minimum ranges are not changed | a weaker dart so the heavy ones take 2 | booker |
| 279 | LT-8: directional armour 60 / 100 / 140 / 160% with the Abrams' 90-degree arcs | other numbers | booker |
| 280 | LT-9: crusher 3 like the Abrams (soldiers and the light cars) | 2 (soldiers only) | booker |
| 281 | LT-10: the Humvee's smoke launchers (2 pops); no Trophy; rearm as the Abrams (a round or a pop every 2 s at a depot or the ammo point, a garage all at once) | — | booker |
| 282 | AL-1: the airlift is an aircraft archetype block (`airlift`: seats 2, tags airliftable, ramp 11.5 cells behind the centre, board range 4 cells, 10 s each, 5 s between drive-offs) and reuses `Enter` (4), `Deliver` (39) and `Exit` (5): no new CommandKind | a LoadVehicles command | booker |
| 283 | AL-2: vehicles board only while it is parked at a station of a main air base (an airfield with unlimited supply); Enter is refused while it flies, taxis or lands and at a forward airbase | anywhere it is parked | booker |
| 284 | AL-3: one up the ramp at a time (the lowest ID of those in reach first); the 10 s run from the ramp; boarding holds it parked until its next order | both at once | booker |
| 285 | AL-4: tanks or containers, never both: the first vehicle up the ramp takes the 4 containers off (free, at the main base); with a seat in use it loads none and refuses Airdrop; emptied, it loads them at home | board only an empty airlifter | booker |
| 286 | AL-5: at the forward airbase's stand the first vehicle drives off 5 s after it parks, the second 5 s later, on the ramp point facing away, to the field garage's rally point (none: they stay); then it flies home to load (the delivery rule) | all at once; stay at the ramp | booker |
| 287 | AL-6: Exit on the parked airlifter (any airfield) lets them all out at once behind it; Exit on a vehicle aboard or its driver is ignored | Exit ignored | booker |
| 288 | AL-7: destroyed with vehicles aboard (in the air or on the ground), they and their crews are lost, no survival roll | a roll on a ground kill | booker |
| 289 | AL-8: vehicles aboard keep their last position (as helicopter riders); renderers draw them in the airlifter | follow the airlifter | booker |
| 290 | AL-9: only the Booker is airliftable (the Abrams is too heavy for 2 a sortie) | the Humvee too | booker |
| 291 | AL-10: the Deliver target is a forward airbase only (an airfield with a stock), as for containers | any airfield | booker |
| 292 | AL-11 (replaced by 315): a Rearm order on an airlifter carrying tanks: its service has nothing to load, so it leaves on its own (the sortie rule) with them aboard | hold it parked | booker |
| 293 | LT-11: no skirmish AI use; no vehicle airdrop (owner) | — | booker |
| 294 | HW-1: range 15 to 80 cells: the spec's "about 800 / 150" read in the content's raw tenths, like the heavy rocket truck's 900 / 500 and the mortar's 300 / 80; the longest gun but the heavy rocket salvo truck's 90 | 800 cells (ten maps across) | howitzer |
| 295 | HW-2: splash 3.5 cells of radius (7 across), 100% to 10%: the spec's "35" read as raw tenths of the radius, between the mortar's 2.5 and the heavy rocket's 5 | 1.75 cells of radius (35 raw across) | howitzer |
| 296 | HW-3: scatter 1 cell per axis at 15 cells to 3 at 80 (raw 10 / 30, the mortar's scheme); vision 4 cells past its footprint (raw 40) | wider scatter | howitzer |
| 297 | HW-4: the heavy rocket's warhead, shared (600 to buildings, 360 to armour, its garrison occupant share): two shells destroy a howitzer | its own warhead | howitzer |
| 298 | HW-5: the crew is the vehicles' crew seat on a structure (one seat, no passengers), not a garrison: the crewman never fires his own weapon; he enters from 1.5 cells of the footprint and leaves past the middle of its south edge | a garrison of one | howitzer |
| 299 | HW-6: the crewman takes 15% of an explosive hit on the gun (the observation post's occupant share) and can die in the seat: "kill the crewman and it falls silent" | safe like a driver: only the gun's destruction kills him | howitzer |
| 300 | HW-7: destroyed, he bails out by the vehicle crew rule (50% to live, 40% of his HP lost) | the observation post's (all live, at most 40% HP) | howitzer |
| 301 | HW-8: a structure's weapons fire from its centre (rings are circles); its sight still counts from its footprint edge | from the footprint edge | howitzer |
| 302 | HW-9: traverse one facing every 8 ticks (half a turn in 6.4 s; facings are 11.25 degrees), it fires within a facing; idle it stays laid | 6 s exactly (finer facings) | howitzer |
| 303 | HW-10: shell 10 cells/s on a 50% arc (8 s at full range); it aims with the usual lead (where a moving target will be on its course), so a target that turns or stops escapes | no lead (at where it stands) | howitzer |
| 304 | HW-11: group preference: the most enemies within 3.5 cells of a candidate, a structure counting 3, after the attacker, threat and autoRange rules; then nearest | structures first, then groups | howitzer |
| 305 | HW-12: Shift+G: one salvo per queued point in turn, round and round until stopped or dry, up to 16 points; for every ground unit on an attack-ground order (the mortar too) | once through the list, then idle | howitzer |
| 306 | HW-13: a unit that can't move refuses a ground point outside its rings; an Attack on a unit out of its reach stays and fires once it comes in | refuse it too | howitzer |
| 307 | HW-14: the truck's order is the existing Unload (12) with the gun as its target (no new CommandKind); parked means idle and still within 15 cells of the footprint; the first shell 2 s after it starts | a new Resupply command | howitzer |
| 308 | HW-15: a truck's load is now 0 to 400: a depot takes a remainder at once (still waiting for room), LoadCargo tops up (the store pays the missing part), garage service already topped up | partial trucks only for guns | howitzer |
| 309 | HW-16: the gun takes shells uncrewed; no depot reload, ammo point, pad or service; no structure gets a depot's reload-rate bonus | crewed only | howitzer |
| 310 | HW-17: the flash spots it for every enemy player for 200 vision passes after the shot, through fog and smoke (targetable: auto and ordered); the marker is the client's (RevealedBy) | only through fog, not smoke | howitzer |
| 311 | HW-18: its shells are the `rocket` interception class (the Patriot type engages them at its rocket numbers); no C-RAM type yet | a shell class nothing intercepts | howitzer |
| 312 | HW-19: 10 x 10 cells, 3 m high, 1,200 HP, 1,500, 40 s, no rubble stage, 75% refund on cancel; glyph `Z`, tag `artillery` | — | howitzer |
| 313 | HW-20: no skirmish AI use; out of shells an attack-ground order ends (the launchers' rule) | — | howitzer |
| 314 | NF-1: a vehicle boarding an airlifter drives to, and its board range counts from, the ramp point or, when its class can't stand on the ramp point's cell (the heavy bay), the nearest cell it can (straight-line, ties by row then column, within 16 cells; 3.04 cells from the ramp in the heavy bay); the second of two Bookers no longer stalls 4.1 cells off | widen the board range; queue the second at a waiting point | night-fixes |
| 315 | NF-2 (replaces 292): an airlifter with a vehicle aboard or on its ramp never leaves on its own after its service: a Rearm order (or any visit's service) refuels and repairs it and it stays parked, held, until an order (Deliver, Move, an air task) | 292's sortie rule (it took off with them) | night-fixes |
| 316 | NF-3: no inflow passes a store's cap: a truck whose room shrank during its unload pours in what fits and keeps the rest, waiting beside the store (its Unload order goes on); the start rule stays (a truck starts only with room for its whole load); pallets (excess lost) and an airlifter's containers (whole ones only) were already capped; loading takes what is left, never below 0 | unload part of a load whenever there is any room | night-fixes |
| 317 | CH-1: the stone farmhouse (`farmhouse_stone`): 7 x 5 cells, 7.2 m, 900 HP (sturdier than the small house's 700 for its thick stone), holds 5; door at (3, 5); glyph `[` | other numbers | homes |
| 318 | CH-2: the unfinished house (`house_unfinished`): 6 x 5 cells, 9.5 m (the open frame upstairs), 800 HP, holds 5; door at (3, 5); glyph `\|` | other numbers | homes |
| 319 | CH-3: the courtyard house (`house_courtyard`): 9 x 9 cells including the walled yard, so the whole yard blocks movement like the house; 5 m, 800 HP, holds 5; the door is the yard gate at (4, 9); glyph `@` | only the house blocks and units walk into the yard | homes |
| 320 | CH-4: the tiled villa (`villa_tiled`): 7 x 6 cells, 9.5 m, 900 HP, holds 6; door at (3, 6); glyph `~` | other numbers | homes |
| 321 | CH-5: the tin-roof shack (`shack_tin`): 4 x 4 cells, 3.2 m, 350 HP (flimsy), holds 2 (1 from damage stage 2 on: 75% and 50% of 2 round down); door at (2, 4); glyph `!` | other numbers | homes |
| 322 | CH-6: every home takes the small house's garrison numbers (+2 cells range, +4 vision, occupants take 25% of explosive hits, stages at 80 / 60 / 35%, capacity 100 / 100 / 75 / 50%, 20% lost when thrown out, collapse 50% to live at 30% HP), one door on the front row centre, neutral, cost 0, build time 600 ticks, building armour; in the Resistance list (sandbox spawns) like the town buildings | per-home garrison numbers | homes |
| 323 | FB-1: the farm shed (`farm_shed`, a barn / animal shed): 8 x 5 cells, 5.5 m, 600 HP, holds 4; one big door at (4, 5); glyph `]` | other numbers; not garrisonable | farm |
| 324 | FB-2: the greenhouse (`greenhouse`, a plastic tunnel): 10 x 4 cells, 3 m, 150 HP (flimsy), nobody garrisons it; glyph `(` | other numbers | farm |
| 325 | FB-3: the hay store (`hay_store`, an open-sided roof on posts over bales): 6 x 4 cells, 5 m, 300 HP, nobody garrisons it; glyph `$` | other numbers; hay that burns | farm |
| 326 | FB-4: the chicken coop (`chicken_coop`): 3 x 3 cells, 2 m, 100 HP, nobody garrisons it; glyph `,` | other numbers | farm |
| 327 | FB-5: the farm water tower (`farm_water_tower`, a tank on legs): 3 x 3 cells, 12 m, 400 HP, nobody garrisons it; glyph `:` | other numbers; a high-ground vision post | farm |
| 328 | FB-6: the house over a shop (`house_over_shop`, a town filler: a shop with a shutter and awning, flats above): 6 x 5 cells, 9.5 m, 1,000 HP, holds 6; door at (3, 5); glyph `}` | other numbers | farm |
| 329 | FB-7: the farm shed and the house over a shop take the homes' garrison numbers exactly (row 322), neutral, cost 0, build time 600 ticks, building armour; all six are in the Resistance list (sandbox spawns) | per-building garrison numbers | farm |
| 330 | FB-8: the greenhouse, hay store, chicken coop and water tower are plain civilian structures (content `neutral: true`, new: no structure without a garrison was neutral before): placed neutral (owner 0, nobody's for good), they block their whole footprint, nobody enters them (an Enter order is refused), nobody picks them as a target on his own (unarmed, empty), building armour; they have no damage stages (the client may show HP-based damage) and at 0 HP they go like a town building: the unit is removed and the footprint stays blocked as rubble | free the footprint when they fall; owned by whoever places them; an airfield-style standing rubble piece | farm |
| 331 | RK-1: four road kinds in the sim (`highway`, `paved`, `gravel`, `dirt`), an optional `kind` on the map's `roads` entries, `paved` by default; the village lane is paved, a track dirt; where roads overlap the faster kind wins | the client's three paint kinds (track, gravel, paved) only; last entry wins | roads |
| 332 | RK-2: wheeled speed on roads (all three wheeled classes): highway 140%, paved 125%, gravel 110%, dirt 100% of the same cell off-road, multiplied with slope and forest | other numbers; heavy trucks gaining less | roads |
| 333 | RK-3: tracked speed on roads: highway 110%, paved 110%, gravel 105%, dirt 100%; no new off-road penalty (open flat ground stays 100% for everyone; tracked already lose less in forest and on slopes) | an off-road penalty for wheels on rough ground | roads |
| 334 | RK-4: infantry: no speed change on any road kind (path cost 90% as before; crowds ignore roads) | +5 to 10% on paved | roads |
| 335 | RK-5: path cost by kind follows the speed, anchored on paved's old cost: wheeled 45 / 50 / 57 / 63%, tracked 70 / 70 / 73 / 77%, foot 90% everywhere (highway, paved, gravel, dirt); dirt keeps a preference although it is no faster | pure travel time (wheeled 71 / 80 / 91 / 100%); no preference for dirt | roads |
| 336 | RK-6: old maps' plain roads are paved: wheeled drive 25% and tracked 10% faster on them than before; routes and hashes of the layers unchanged; airfield pavement is paved | keep plain roads at today's speed (a kind "legacy") | roads |
| 337 | WA-1: water in the map file: a `water` list of rivers (a centre line of cells, upstream first, `width` in cells, a point may give its own as `[x, y, w]`) and lakes and the sea (a `polygon` in map coordinates, cell corners); each body one flat surface `height` in tenths of a metre (as pads); a river that falls is drawn as several reaches, each its own body | per-point river heights; a water level read off the heightmap | water |
| 338 | WA-2: the shore rule: a cell is water when its centre lies in the water or on its edge; a river of odd width w covers w cells across a straight stretch, an even width w + 1 (water is never narrower than drawn, so no unit stands in drawn water) | strictly inside (even widths w - 1); any cell the water touches | water |
| 339 | WA-3: open water is impassable to every ground class (foot, all wheeled, tracked): path cost 0 and speed 0 for each, and `Terrain.Water` on the grid (not passable, but shots fly over it and sight crosses it, unlike rock); no swimming, no amphibious units; aircraft unaffected | fords (shallow water crossed slowly) | water |
| 340 | WA-4: nothing is put in open water: build sites, the sandbox's spawns (a structure is refused, a unit goes to the nearest dry cell), mines, a scenario's armies, units leaving a vehicle, a factory, a crash or a helicopter (the grid's nearest dry cell); `World.Spawn` of a ground unit on water stands it on the nearest dry cell | refuse the spawn | water |
| 341 | WA-5: over water the surface counts, not the bed: `HeightAt` is the higher of the two (sight lines and fog, shells' arcs, bombs, wrecks, aircraft heights and the flight planner's clearance); the bed is `GroundHeightAt` (the client draws it under the surface) | sight over the bed | water |
| 342 | WA-6: a blast in open water (a shell, rocket, missile, bomb, drone, wreck: anything going off on the surface, not on a unit) digs no crater (not even the bunker buster's deep crater) and changes no ground; its splash hurts what is in reach as on land (a unit on the bank, later on a bridge); the events carry `Water` | damped splash in water | water |
| 343 | WA-7: a helicopter crash-landing in open water sinks and nobody gets out (no swimming); a pilot ejecting over water steers his chute to the nearest dry cell within 16 cells as before, else he is lost; an airdrop pallet landing in water is lost | the crew swims ashore; a pilot over water always lost | water |
| 344 | WA-8: the bridge hook: a deck bit per water cell (flags bit 7, `World.SetDeck` / `TerrainMap.SetDeck`): a decked cell costs what the same dry cell would (its road too) and is clear on the grid; lifting it makes it open water again. Bridges (task 5) set and lift it; a deck's height and what happens to units on a deck that falls are theirs | a bridge structure blocking and unblocking cells itself | water |
| 345 | WA-9: where bodies overlap the later one in the list holds the cell; no forest grows in water (cleared at load); a road over water keeps its kind in the flags (for a deck to reuse) but costs 0 without a deck | the lowest surface wins | water |
| 346 | F35-1: `stealth_multirole` "Stealth multirole", class M; built only by the main air base's hangar **(owner)**: the forward airfield's facility excludes it (tag `stealth_jet`, `production.excludeTags` now read for producers into a station); **no cap (owner)**; it lands and is serviced at any of its side's airfields taking class M | the forward airfield builds it too (drop the exclude) | f35 |
| 347 | F35-2: 3,200, 55 s, 300 HP, 3.75 min of fuel at cruise (the multirole 3.5, the fighter 4), 40 cells of sight, a 150-cell air radar (between the multirole's 110 and the fighter's 160); placeholders | — | f35 |
| 348 | F35-3: the multirole's airframe (envelope, turns, runs, elements, pursuit, break, pattern, taxi, crash and ejection), 10.7 x 15.7 m, a round in 4 s; the `fighter` air class (its signature does the rest); Guard: vehicles, structures, aircraft, drones | an air class of its own | f35 |
| 349 | F35-4: stealth mode's speed **1.3x the multirole's (owner)** on cruise, attack and dash (26 / 29.9 / 36.4 cells/s; on big maps on top of the map's growth), the same g: a 24-cell turn at cruise (fly zone 74 cells); a strike's runs keep the content's attack speed (23, D-SPD1's rule), its transit flies the faster cruise | runs at 1.3x too | f35 |
| 350 | F35-5: stealth signature detect 35, lock 250 **(owner)**, kill 65 (between the fighter's 60 and the B-2's 70); heavy detect 80, lock 130 **(owner: to tune)**, kill 90 | other kill percents | f35 |
| 351 | F35-6: the modes' loads **(owner)**: stealth 2 bombs + 10 missiles, heavy 10 + 2; the missiles are the fighter's radar missile (`f35_aam`, an AIM-120 type); no IR missile, no gun | a mix of IR and radar missiles; the F-35A's gun | f35 |
| 352 | F35-7: the bomb is the multirole's 2,000 lb (its heavier preset, "heavy bombs") x 1.3 = 1,690 **(owner: +30%)**, a blast of 4 / 10 cells (the 2,000 lb's 3 / 8) **(owner: bigger)**, its dive delivery; fire modes `single` (default) and `pair`; strike tier 6 (the 2,000 lb look) | the 500 lb x 1.3 (910, tier 5); a `salvo4` mode | f35 |
| 353 | F35-8: 8 flare salvos in both modes **(owner: the multirole's count)**; no chaff (the owner named flares; radar jamming is out) | the multirole's 6 chaff | f35 |
| 354 | F35-9: the change happens **only parked (owner)** at a station of its side's airfield that rearms now with free supply: a stand or a shelter of a main air base or a forward airfield (a shelter with its facility up); never in the air, holding, landing or taxiing, never at a forward airbase (limited stock) | the main air base only; the forward airbase too, paying its stock | f35 |
| 355 | F35-10: it takes **200 ticks (owner: about 10 s)** whatever the station's rate; it holds the aircraft at its station (an order meanwhile launches it once done); ordering its own mode cancels; the station falling cancels | an order cancels the change | f35 |
| 356 | F35-11: the change is the reload: it ends with the new mode's full load of every weapon (no Rearm needed; what it carried is taken off); flares untouched (the same in both modes) | the change empties it and a Rearm loads the new mode | f35 |
| 357 | F35-12: a new `CommandKind.SetLoadout` (42, `Index` the mode): no existing order fits (`SetFireMode` and `SetBombWeapon` are per bomb weapon, `SetWeaponMode` an enum of fire rules); it is built in stealth mode (the first) | reuse `SetBombWeapon` | f35 |
| 358 | F35-13: radar spotting's coverage is the radar's full air vision (slant from its eye, not scaled by the aircraft's signature), only while the radar works (the mobile radar set up); any airborne phase counts (holding, climb-out), either mode **(owner)** | its ground distance; free flight only | f35 |
| 359 | F35-14: radars that count by the tag `air_radar`: the mobile radar (the SA-6 battery's radar) and the air defence vehicle **(owner)**; not the AA soldier **(owner)**; the Sentinel's radars (radar truck, base radar, radar site) carry no tag (the brief names the Resistance's) | tag them for a Sentinel mirror match | f35 |
| 360 | F35-15: revealed = spotted for its side (through the fog, targetable by all) **(owner)**: the radar and the ground units within 2 cells; under fog the grid's cells within 2 cells of the radar's cell (radius 2 cells, 13 cells in all) **(owner: a very small circle)**; 200 ticks after the last tick inside **(owner: about 10 s)** | 1 cell; the radar alone | f35 |
| 361 | FA-1: leaving 30% faster and returning 50% faster than now **(owner)**, at every airfield type for every runway aircraft **(owner: all aircraft)**: content `tempo` 130 / 150 on each airfield type, not each aircraft's speeds edited (the taxi out and in share one speed) | per-aircraft numbers; per-type values | faster-airfield |
| 362 | FA-2: the tempo time-scales a phase (speeds times k, accelerations and turns times k^2): the same ways and pattern, sooner; in the pattern's turns a jet pulls k^2 its own a(S) (2.25 at 1.5). Faster speeds alone do not help: above its corner speed a jet turns wider and slower (a bigger pattern, no quicker) | speeds up to corner speed and a shorter pattern | faster-airfield |
| 363 | FA-3: the takeoff roll 1.3 times the acceleration to the same lift-off speed (a shorter roll); dry or afterburner still chosen on its own roll; the climb-out unchanged | lift off 1.3 times faster too; choose on the shorter roll | faster-airfield |
| 364 | FA-4: "airborne" is wheels-up (as `HeavyBayTests` has it), "returning" from the recall (or bingo) to parked | to free flight | faster-airfield |
| 365 | FA-5: the flight home and the hold at 1.5 times cruise; bingo still counts the flight home at cruise (it goes home where it did, arriving with more fuel) | count the faster flight (later bingo: more time on station) | faster-airfield |
| 366 | FA-6: the approach sequence **(owner: the next may land as soon as the one ahead is down and off the runway)**: cleared from the hold once the one ahead has left its join; leaves its join once the one ahead is down; takes the runway as that one turns off; at its fix without it, a go-around | clear only at the touchdown of the one ahead (about 25 s apart); land behind a roll-out | faster-airfield |
| 367 | FA-7: at most one waits on its join, circling it (the overhead's join circle at the initial point, the straight-in's at its entry point), not in the hold | extend the run-in or the downwind leg | faster-airfield |
| 368 | FA-8: departures only while the runway is free and nobody is cleared to land (landings first, as before) | a departure between two landings | faster-airfield |
| 369 | FA-9: holds **(owner: smaller circles closer to the base)**: forward airfield 16 round 20 out, main air base and forward airbase 24 round 30 out (heights as before); `holding.reach` keeps the clearance distance (50 / 60 cells) | twice the new radius (cleared later: the bomber's join 11 s) | faster-airfield |
| 370 | FA-10: unchanged: the runway lock's 2 s after wheels-up, the lane wait, the pivot from a standstill, the facing a tick | scale them by the tempo too | faster-airfield |
| 371 | FA-11: helicopters get the same tempo on their rotary block: spool-up and lift-off 1.3x, the flight home (time-scaled) and the descent onto the pad 1.5x; yaw unchanged | leave them (they never queue) | faster-airfield |
| 372 | FA-12: one runway per airfield: no diversion to a linked field's runway, no intersection departures (not in the plan) | either, later | faster-airfield |
| 373 | BR-1: three bridges, sizes fixed with the art: `bridge_stone` 3 x 8 cells (1 lane), `bridge_concrete` 5 x 12 (2 lanes), `bridge_metal` 9 x 18 (4 lanes); footprint w across, h along the road (the art's glTF Z), so as laid out the road runs north-south and a bridge facing East or West stands turned (east-west); the four axis directions only | arbitrary angles (no placement system for them) | bridges |
| 374 | BR-2: HP stone 2,500, concrete 3,500, metal 4,500, building armour (5 one-way drones or 3 Kaman drones bring any down; a 500 lb bomb takes 1,400) | other numbers; an armour class of its own | bridges |
| 375 | BR-3: the road on it: stone `paved` (the village lane), concrete `paved`, metal `highway`, laid on the whole footprint (the ramps too) whatever road the map has there; its deck is flat (grade 0) whatever the bed | only the deck cells; the faster kind wins as where roads cross | bridges |
| 376 | BR-4: the map places them: a `bridges` list in the terrain JSON (`kind`, `cell` the footprint's top-left cell as it stands, `dir` `ns` or `ew`), put up when the terrain is attached with the first player's definition (both factions list the three); the sandbox spawns them too | the client's town file through the sandbox spawn (sandbox matches only) | bridges |
| 377 | BR-5: a site must have both end rows on dry land, its road's centre line crossing water, the footprint on the map, its dry cells free of rock and buildings and no structure or other deck under it; else the map fails to load | any water cell under it; a full row of water across it | bridges |
| 378 | BR-6: never built by players **(owner)**: content refuses a bridge with `construction`, so a Build order places nothing | — | bridges |
| 379 | BR-7: neutral for good (owner 0) and blocking nothing: no movement box, no routing or grid block; shots and dives pass over it onto what stands on it (a shot or guided bomb at it hits it); nothing else is placed on it; a bunker buster digs no deep crater on its deck | a hard box with the deck as a lane | bridges |
| 380 | BR-8: stages by HP: 0 above 70%, 1 above 40%, 2 (heavy, passable) down to 0 HP, 3 the span down | other thresholds | bridges |
| 381 | BR-9: at 0 HP the span falls: the deck is lifted (the ramps on the banks stay ground) and every ground unit on the deck dies with everyone inside it (a helicopter crash in water's rule, WA-7: nobody swims, no bail-out rolls); units on the dry ramps live | units on it dropped into the water and drowned after a delay; a swim ashore | bridges |
| 382 | BR-10: the ruin stays at 1 HP, never removed, no target for orders while down (attack orders end, new ones are refused); blasts still hurt it, setting its repair back | an attackable ruin; a ruin blasts can't touch | bridges |
| 383 | BR-11: repair by builders (workers, the bulldozer; engineers have no builder) of either side with the Repair order: one restores the whole bar in the build time (stone 30 s, concrete 45 s, metal 60 s), a 2nd adds half, a 3rd nothing; the deck is laid again at 40% of max HP (one builder from a fall: 12 / 18 / 24 s), standing damaged (stage 1) | reopen at 25% (heavy, stage 2); only the side that holds the ground; slower | bridges |
| 384 | BR-12: a span falling cuts every path across it short of the water: a unit on its own route asks for a new one at once, a group's flow is re-planned when routing sees the change | wait for stuck detection | bridges |
| 385 | BR-13: "being repaired" (`BridgeState.Repairing`, the scaffold) only while builders add work this tick, not while they walk there | while any builder has the order | bridges |
| 386 | BR-14: no weight limit **(owner)**: every ground class crosses every bridge (the widest vehicles need 3 cells: the stone bridge's width); the sim keeps the deck at the water surface (`HeightAt` unchanged), the client draws it 2.5 m above the bank | a deck height in the sim | bridges |
| 387 | BR-15: nobody targets a bridge on his own (neutral, as the town buildings); players order attacks; no skirmish AI use | the AI cutting bridges | bridges |
| 388 | AF-1: two new road kinds in the map's `roads[].kind`: `alley` (old-town streets) and `footpath` (worn paths); `RoadKind.Alley` = 5 and `Footpath` = 6 are appended to the enum, nothing renumbered; an unknown kind is still a load error. Decision to confirm | a separate `paths` list; a flag on the road entry | alleys |
| 389 | AF-2: an alley is passable only to the foot movement class (locomotor foot); every other ground class (light wheeled, wheeled, heavy wheeled, tracked) has cost 0 and speed 0 there, a wall, so routing, flow fields, clearance blocks and movement need no special case; derived from the locomotor, no new `rules.json` field. Decision to confirm | a per-class `alleys` flag in the rules; light 4x4s and the dirt bike allowed in | alleys |
| 390 | AF-3: for foot an alley costs what the foot class's paved road costs (90%) and changes no speed (334 kept); no values of their own, so `roadCost` / `roadSpeed` stay four numbers and old rules files load and hash as before. Decision to confirm | its own cost; +speed on stone | alleys |
| 391 | AF-4: a footpath blocks and slows nobody: vehicles get plain ground (cost and speed of the cell beside it); foot pays paved's road cost (90%) and goes no faster (334). The preference holds for groups of soldiers too: routing's "crowds ignore roads" undoes every road discount but a footpath's. Decision to confirm | a 95% preference; crowds ignore footpaths like roads | alleys |
| 392 | AF-5: where kinds overlap a vehicle road wins the cell whatever the order the map lists them in (a footpath or alley crossing a paved road leaves the road's speed and passability alone); an alley beats a footpath. Decision to confirm | the foot kind wins; last entry wins | alleys |
| 393 | AF-6: storage: a second per-cell byte (`_flags2`; bits 0-1 the kind, bits 2-7 spare), allocated and hashed only on a map with a foot-kind cell, so every map without hashes exactly as before. Decision to confirm | a wider flags word for every map; a cell list | alleys |
| 394 | AF-7: no structure on an alley (build and so the AI, the sandbox, airfield pieces, tunnels, bridge sites: a map bridge on one fails the load); a ground vehicle spawned or produced on one stands on the nearest cell that is not an alley (rings, like water); soldiers stand and lay mines in alleys; footpaths restrict nothing. Decision to confirm | the grid marks alleys blocked for everyone; refuse the spawn | alleys |
| 395 | AF-8: client accessor: `RoadKindAt` returns `Alley` / `Footpath` where no vehicle road covers the cell, `TerrainMap.IsFootOnly(cell)` and `World.IsAlley(cell)` answer for alleys, `TerrainMap.Road(cell)` stays the vehicle roads only. Decision to confirm | `Road` true for every kind | alleys |
| 396 | AF-9: `SetRoadKind` lays exactly the kind it is given, a foot kind too (a vehicle road replaces it and it replaces a vehicle road); an airfield's pavement (`SetRoad`) replaces a foot kind. Decision to confirm | pavement refused on alleys | alleys |
| 397 | CH-7: the shepherd's hut (`shepherd_hut`): 4 x 4 cells, 3.5 m, 400 HP (between the tin shack's 350 and the small house's 700), holds 2; door at (2, 4); glyph `{`. Decision to confirm | other numbers | homes |
| 398 | CH-8: the mountain house (`mountain_house`): 6 x 6 cells, 9.2 m, 900 HP, holds 5; door at (3, 6); glyph `<`. Decision to confirm | other numbers | homes |
| 399 | CH-9: the mud-brick house (`mudbrick_house`): 6 x 5 cells, 4.3 m, 600 HP (a little below the small house's 700), holds 4; door at (3, 5); glyph `>`. Decision to confirm | other numbers | homes |
| 400 | CH-10: the three second-batch homes take the first batch's rules exactly (row 322): the small house's garrison numbers (+2 cells range, +4 vision, occupants take 25% of explosive hits, stages at 80 / 60 / 35%, capacity 100 / 100 / 75 / 50%, 20% lost when thrown out, collapse 50% to live at 30% HP), one door on the front row centre, neutral by their garrison, cost 0, build time 600 ticks, building armour; in the Resistance list (sandbox spawns). Decision to confirm | per-home garrison numbers | homes |
| 401 | TB-1: the corner building (`corner_building`, a building on a street corner): 7 x 7 cells, 10.5 m, 1,100 HP, holds 6; door at (3, 7); glyph `1`. Decision to confirm | other numbers | town buildings |
| 402 | TB-2: the workshop (`workshop`, a low workshop): 7 x 6 cells, 6 m, 800 HP, holds 4; door at (3, 6); glyph `2`. Decision to confirm | other numbers | town buildings |
| 403 | TB-3: the three-floor apartment block (`apartment_3f`): 10 x 6 cells, 10.5 m, 1,400 HP, holds 8; door at (5, 6); glyph `3`. Decision to confirm | other numbers | town buildings |
| 404 | TB-4: the L-shaped apartment block (`apartment_l`): 12 x 10 cells, 10.5 m, 1,800 HP, holds 10; door at (6, 10), the yard gate; glyph `4`. Its whole 12 x 10 footprint blocks movement, the inner yard of the L included, exactly as the courtyard house (`house_courtyard`) blocks its walled yard: the sim's footprint is one rectangle, so nobody walks into the yard; the door is the cell just outside the front row centre. Decision to confirm | block only the two wings and leave the yard passable (needs a footprint mask the sim does not have); a door inside the yard | town buildings |
| 405 | TB-5: the office block (`office_block`, a tall office block): 8 x 8 cells, 13 m, 1,600 HP, holds 8; door at (4, 8); glyph `5`. Decision to confirm | other numbers | town buildings |
| 406 | TB-6: the kiosk (`kiosk`, a small street kiosk): 2 x 2 cells, 3 m, 150 HP (flimsy, like the greenhouse), nobody garrisons it; content `neutral: true`, glyph `6`. A plain civilian prop like the chicken coop (row 330): placed neutral, blocks its footprint, an Enter order is refused, no damage stages, at 0 HP the unit goes and the footprint stays blocked as rubble. Decision to confirm | other numbers; a one-man booth that can be garrisoned | town buildings |
| 407 | TB-7: the five garrisonable town buildings take the homes' rules exactly (row 322): the small house's garrison block (+2 cells range, +4 vision, occupants take 25% of explosive hits, stages at 80 / 60 / 35%, capacity 100 / 100 / 75 / 50%, 20% lost when thrown out, collapse 50% to live at 30% HP), one door on the front row centre, only the capacity differs; all six are neutral, cost 0, build time 600 ticks, building armour, in the Resistance list (sandbox spawns); glyphs are the digits `1` to `6` (no archetype used a digit before). Decision to confirm | per-building garrison numbers; letters or symbols for the glyphs | town buildings |
| 408 | TB-8: the school (`school`, a two-floor school at the back of a walled yard): 12 x 10 cells, 8 m, 1,600 HP, holds 10; door at (6, 10), the yard gate on the front row centre, 1.02 m from the art's `door_01` (1.0 m across, the half cell an even width leaves, and 0.2 m along); glyph `7`. The whole 12 x 10 footprint blocks movement, the walled yard included, exactly as the courtyard house blocks its yard (row 404): the sim's footprint is one rectangle. The art's `door_02` (the porch, inside the yard) is not an engine door. Decision to confirm | other numbers; block only the school and leave the yard passable (needs a footprint mask the sim does not have) | town buildings |
| 409 | TB-9: the clinic (`clinic`, a clean two-floor clinic): 8 x 7 cells, 7.5 m, 1,200 HP, holds 6; door at (4, 7), the front row centre just outside the footprint; glyph `8`. Not a match with the art: its `door_01` (the double glass door on the landing) stands inside the footprint, 7.9 m behind the front row, so the sim's door (it must be a cell just outside the footprint) is 7.96 m from it (1.0 m across and 7.9 m along). Decision to confirm | other numbers; the art's `door_01` moved to the front row centre outside the footprint | town buildings |
| 410 | TB-10: the warehouse (`warehouse`, a steel portal-frame shed): 12 x 8 cells, 8 m, 1,400 HP, holds 8; door at (6, 8), the front row centre just outside the footprint; glyph `9`. Not a match with the art: its `door_01` (the personnel door) stands inside the footprint, 3.7 m behind the front row, so the sim's door is 3.83 m from it (1.0 m across and 3.7 m along); the roll-up doors (`door_02` and the dock) are not engine doors. Decision to confirm | other numbers; the art's `door_01` moved to the front row centre outside the footprint | town buildings |
| 411 | TB-11: the fuel station (`fuel_station`, a canopy over two pump islands with a small shop; only the shop is garrisoned in the art): 10 x 8 cells, 5.5 m (the canopy top), 700 HP (the lightest of the five), holds 3; door at (5, 8), the forecourt edge on the front row centre, 1.02 m from the art's `door_01` (1.0 m across and 0.2 m along); glyph `0`. The whole 10 x 8 footprint blocks movement, the forecourt included. The shop's glass door (`door_02`) is not an engine door. Decision to confirm | other numbers; block only the shop and canopy posts (needs a footprint mask) | town buildings |
| 412 | TB-12: the factory (`factory`, a brick hall with a saw-tooth roof, an office annex and a chimney): 14 x 10 cells, 9 m (the roof; the chimney reaches 18 m, ignored), 2,000 HP (the strongest of the batch), holds 10; door at (7, 10), the yard gate on the front row centre, 1.02 m from the art's `door_01` (1.0 m across and 0.2 m along); glyph `)`. The whole 14 x 10 footprint blocks movement, the fenced yard included. The hall's and the annex's doors (`door_02`, `door_03`) are not engine doors. Decision to confirm | other numbers; the chimney's 18 m as the height | town buildings |
| 413 | TB-13: the market stalls (`market_stalls`, two rows of four stalls): 8 x 6 cells, 3.5 m, 200 HP (flimsy, like the greenhouse), nobody garrisons them; content `neutral: true`, glyph `?`. A plain civilian prop like the kiosk (row 406): placed neutral, blocks its footprint, an Enter order is refused, no damage stages, at 0 HP the unit goes and the footprint stays blocked as rubble. Decision to confirm | other numbers; the aisle between the rows left passable (needs a footprint mask) | town buildings |
| 414 | TB-14: the town water tower (`water_tower_town`, a concrete shaft with a wide tank): 4 x 4 cells, 18 m, 600 HP (above the farm tower's 400), nobody garrisons it; content `neutral: true`, glyph `'`. A plain civilian prop like the kiosk: placed neutral, blocks its footprint, an Enter order is refused, at 0 HP the footprint stays blocked as rubble. Decision to confirm | other numbers | town buildings |
| 415 | TB-15: the cell tower (`cell_tower`, a 30 m lattice mast on a fenced pad): 3 x 3 cells, 30 m, 400 HP, nobody garrisons it; content `neutral: true`, glyph `` ` ``. A plain civilian prop like the kiosk: placed neutral, blocks its footprint, an Enter order is refused, at 0 HP the footprint stays blocked as rubble. Decision to confirm | other numbers | town buildings |
| 416 | TB-16: the five garrisonable buildings of the fourth batch take the homes' rules exactly (row 322): the small house's garrison block (+2 cells range, +4 vision, occupants take 25% of explosive hits, stages at 80 / 60 / 35%, capacity 100 / 100 / 75 / 50%, 20% lost when thrown out, collapse 50% to live at 30% HP), one door on the front row centre just outside the footprint, only the capacity differs; all eight are neutral, cost 0, build time 600 ticks, building armour, in the Resistance list (sandbox spawns); glyphs are the unused digits `7`, `8`, `9`, `0` and the unused symbols `)`, `?`, `'` and `` ` `` (the only other unused characters, the quote and the backslash, need escaping in JSON; a test checks that the fourteen town buildings of the third and fourth batches share their glyph with no archetype). Decision to confirm | per-building garrison numbers; other symbols for the glyphs | town buildings |
| 417 | MS-1: the map cap is 8,192 cells a side (`TerrainMap.MaxSide`, 16.4 km), width and height alike, any aspect (6,000 x 3,000 for the first big map); the loader, `TerrainMap` and `Grid` refuse 8,193 with "width and height want 1 to 8192 cells, got WxH" ("Map size") | 16,384 (every index still fits, four times the memory); 6,144 only | map cap |
| 418 | MS-2: the height range stays ±256 m (corners are ints since 882c057; the game's map-format "-64 to 63 m" is out of date); raising it is `MaxCornerRaw` and two loader messages, nothing overflows to about ±2^42 raw | ±1,024 m for a real mountain backdrop inside the map | map cap |
| 419 | MS-3: the big-map memory diet, same results and hashes: HPA* sectors keep only their pruned edges (an int each), regions a byte a cell (a sector with 255 or more keeps ushort labels) shared by a crowd's class and its own, one centre-height array in the terrain for routing and fog, fog's rays read the terrain (no per-cell tables), byte formation marks, spatial and box buckets allocated when used: 6,000 x 3,000 with fog, every class's regions and a battle about 890 MB | keep the copies (about 1.5 times the memory) | map cap |
| 420 | MS-4: a tick costs the units, not the map: the movement bucket grid fills and empties only the buckets in use; fog re-sweeps only the viewers whose box reaches a terrain change (the change journal) | - | map cap |
| 421 | MS-5: a far route on a big map arrives: where the abstract search gave up on a reachable goal (40,000 portals) it searches again with twice the greedy weight, up to 8 times, and the corridor search may expand 16 a sector; up to 1,600 x 1,600 cells routes are exactly as before | a second level of routing (a graph of sectors) or a landmark heuristic, which would also remove the cold-sector spike (follow-up) | map cap |
| 422 | MS-6: `World.PrepareRouting(everySector: false)`: the routing grids and every class's regions, no sectors, for a big map's loading screen (every sector takes 102 s at 6,000 x 3,000); the client keeps `PrepareRouting()` below about 2,000 cells a side. Decision to confirm | build the sectors round the starting positions too; a bigger quiet-tick warm-up on big maps | map cap |
| 423 | MS-7: the terrain digest keeps its bytes and value (existing maps hash as before), only its loops are tighter (3 times faster) | a digest by blocks, cached per block (cheap after a change, but every map's digest would change) | map cap |
| 424 | SV-1: the stone rowhouse (`stone_rowhouse`, a stone house built to stand wall to wall in a row): 4 x 6 cells, 8 m, 900 HP, holds 4; door at (2, 6), the front row centre just outside the footprint, 1.02 m from the art's `door_01` (1.0 m across, the half cell an even width leaves, and 0.2 m along); glyph `L`. The footprint is the house with no yard, so rowhouses placed with touching footprints (one every 4 cells) stand wall to wall: together they block movement as one wall, each garrisons and empties through its own door, and destroying one leaves its neighbours as they were (its own footprint stays blocked as rubble). Decision to confirm | other numbers | stone village houses |
| 425 | SV-2: the arched stone house (`stone_house_arch`): 6 x 6 cells, 7.5 m, 1,000 HP, holds 5; door at (3, 6), the front row centre just outside the footprint, 1.02 m from the art's `door_01` (1.0 m across, the half cell an even width leaves, and 0.2 m along); glyph `Q`. Decision to confirm | other numbers | stone village houses |
| 426 | SV-3: the stone tower house (`stone_tower_house`): 5 x 5 cells, 11 m (the tallest home), 1,300 HP (the strongest of the three), holds 5; door at (2, 5), the front row centre just outside the footprint, 0.2 m from the art's `door_01` (0.0 m across, an odd width puts the cell centre on the door, and 0.2 m along); glyph `I`. Decision to confirm | other numbers | stone village houses |
| 427 | SV-4: the three stone village houses take the homes' rules exactly (row 322): the small house's garrison block (+2 cells range, +4 vision, occupants take 25% of explosive hits, stages at 80 / 60 / 35%, capacity 100 / 100 / 75 / 50%, 20% lost when thrown out, collapse 50% to live at 30% HP), one door on the front row centre just outside the footprint, only the capacity differs; all three are neutral by their garrison, cost 0, build time 600 ticks, building armour, the whole footprint blocks movement, in the Resistance list (sandbox spawns). Glyphs: no free single character is left (the compiler allows `!` to `~` except `.`, `#`, `+`, `*`; the quote and the backslash, the only ones no archetype uses, need escaping in JSON), so they reuse letters that only one unit uses and no building or home does: `L` (the light rocket truck), `Q` (the heavy rocket truck), `I` (the main battle tank); a test checks that no other building shares them and that the three differ. On SimRun's text map such a house and that unit are drawn with the same letter. Decision to confirm | per-building garrison numbers; glyphs the quote or the backslash (escaped in JSON); a two-character glyph (a compiler change) | stone village houses |
| 428 | Movement box queries reuse the wall-reach buckets; PushOut merges candidate indices in ascending order and re-queries after every candidate at the updated position, retaining the full-scan result even across large chained pushes. Arbitrary body radii query extra buckets; no per-call allocations. Decision to confirm | a separate tight box index; a bounded displacement assumption | `971c1f0` |
| 429 | Routing refresh keeps its symmetric conservative reach, now `MaxWidth - 1` (4 today), derived from the clearance block's anchor and offset; free-square recurrence repair stays independent. Decision to confirm | tighter asymmetric bounds per class | `971c1f0` |
| 430 | The `depots` movement benchmark adds 300 depots and 3,000 moving units in ten mixed groups crossing an industrial yard. Decision to confirm | garage-heavy workload; more crowded lanes | `971c1f0` |
| 431 | Stuck units far from their destination try a local A* around stationary bodies before the old re-route: hard combined-radius clearance, a soft outer band, no dynamic HPA* rebuild. The prefix retains route tail, flow mode, group and formation slot; detour end and retry tick are hashed. Decision to confirm | a global dynamic occupancy grid; pushing enemies (rejected) | `45b9ef8` |
| 432 | Stuck detour defaults in `unit_ai.json`: 4 searches per tick in unit ID order, radius 12 cells, extra band cost 100 (flat cost 10), retry after 80 ticks; all validated and content-hashed. Decision to confirm | tune budget, radius, cost or retry after QA | `45b9ef8` |
| 433 | Stuck QA uses the standard mixed lobby plus distributed infantry, crewed vehicles and anchored blockers; hold fire keeps armies alive. Seeded small groups move/attack-move every 100–300 ticks. A rolling 600-tick window counts less than 512 raw distance improvement, once per order, with forward-body causes. Decision to confirm | destructive combat fuzzer; a different crowd density | `cfe4997` |
| 434 | Skirmish AI remembers only visible enemy structures, sorted by ID (archetype, cell, last-seen tick); waves without a visible enemy prefer the remembered structure nearest home, ties by ID. Forget only when its old cell is visible and the structure is absent from visible enemies. Decision to confirm | no memory; forget hidden destruction immediately (rejected) | `e18ace8` |
| 435 | Skirmish waves search reachable 16-cell region probes using their own side's visible/explored fog history: unseen/oldest first, then far half, distance from home and region index. Idle wave members reserve distinct probes; seen probes wait 1,200 ticks before re-scouting (`RegionSizeCells`, `RescoutEveryTicks`, init properties). Wave period and size stay unchanged. AI memory is outside the simulation; its queued commands determine hashed game state. Decision to confirm | separate scouts; coarser regions; change the re-scout interval | `e18ace8` |
| 436 | A scouting wave explicitly attacks a currently visible passive structure once its weapon reaches it; passive structures are excluded from unit auto-acquisition, so attack-move alone would walk to the remembered cell and stall. No target ID is taken from hidden memory. Decision to confirm | change the shared unit AI (rejected); continue passing passive structures | `e18ace8` |
| 437 | Supply uses a fresh post-production spatial window for recipient queries when at least eight suppliers are active; smaller counts retain recipient scans. Candidates are ID-sorted and deduplicated before the unchanged radius/owner/state tests, preserving finite-stock allocation and restocking. The threshold is a performance heuristic only. Unit references are cleared at phase end while bucket capacity is retained. Decision to confirm | scan all recipients; rebuild for every supplier count | `b383f49`, `6270a7c` |
| 438 | Supplier/depot candidates are collected in ID order while resetting reload bonuses; NearDepot and DepotWithStock keep their exact eligibility and first-stocked-ID rules. The list includes inactive and storage suppliers so no original predicate is silently removed. Caches are rebuilt each supply phase and never hashed; golden unchanged. Decision to confirm | repeated whole-unit depot scans | `b383f49` |
| 439 | Trunks: one per 2 x 2 slot at most, the cell and a roll picked by a fixed-salt integer hash (`TreeTrunks.SlotHash`, pinned); a trunk stands where the roll is under `trunkChance` of the cell's density, 0 / 0 / 50 / 85 % (none / scrub / grove / dense: a trunk in 12.5% of a grove's cells, 21.25% of dense forest's; the first take was 0 / 0 / 35 / 70: 8.8% and 17.5%, denser woods now). A fixed salt, not the terrain digest, so editing a map elsewhere never moves its trees. A bit array (the flags byte is full), not in the digest. **The owner accepted the defaults on 2026-10-25 (night run), still open to tuning** | a chance per cell (clumps, a blurrier passable line); a per-map salt; other densities | `tree-trunks` |
| 440 | What a trunk is per class (`trunks`): foot `ignore`, wheeled and light wheeled `avoid` (a wall), tracked and heavy wheeled `push`. A trunk blocks its whole cell (as rock), so routing and movement agree; a jeep stops up to ~0.8 cell short of the drawn trunk. Decision to confirm | a trunk circle in movement only (routing and movement disagree: wedging) | `tree-trunks` |
| 441 | `pushCost` 300 (a pusher's cost of a trunk's cell, percent of the cell's own): it goes between trees when the way round is about 2 cells longer at most, pushes otherwise. Decision to confirm | lower: pushes more; higher: detours more | `tree-trunks` |
| 442 | Pushing: a pusher that drove this tick and overlaps a trunk's cell ahead of it (nearest point in front of its heading, or under its centre) presses on it; the lowest ID on a trunk is its pusher. It drives into the trunk at no more than `pushSpeedPercent` 30 from the next tick (the tree is no wall to it), the push counts as progress for stuck detection, and after `pushTicks` 30 (1.5 s) of contact in a row the tree falls; contact lost, it starts over. Decision to confirm | the trunk a wall until it falls (the pusher stops at it); other speed or time | `tree-trunks` |
| 443 | The fall: the pusher's facing snapped to 8 directions; the trunk lies from the cell beside the stump for `fallenLengthCells` 4 (8 m; diagonals 11 m), stopped by the map's edge, a building, rock or water. Decision to confirm | away from the pusher's centre; a longer trunk | `tree-trunks` |
| 444 | `fallenTicks` 600 (30 s): expired in the tree step of that tick, after movement; its cells clear. Decision to confirm | longer; trunks that stay | `tree-trunks` |
| 445 | `crossFallen` true for tracked and heavy wheeled (at their push speed and cost), false for the rest (code default false). A tree falls the way its pusher drives, so a pusher that can't cross fallen trunks walls itself in: a tank through 20 cells of dense forest gave up at x 29.7 after 11 trees with false, was through in 606 ticks after 4 with true; a heavy truck 1,867 against 949 ticks. **The owner accepted the defaults on 2026-10-25 (night run), still open to tuning: tracked and heavy wheeled roll over fallen trunks** | false: tanks need gaps or another way after each push | `tree-trunks` |
| 446 | Nobody is hurt under a falling trunk (yet); a vehicle in its cells is pushed out like from a wall the next tick; units on their own route whose way crosses it re-route (a group's flows re-plan when routing sees it). Decision to confirm | let them leave first | `tree-trunks` |
| 447 | Dense forest no longer stops tracked and heavy wheeled (`forestBlockedFrom` gone): the undergrowth costs and slows them, tracked 180% and 60%, heavy wheeled 280% and 35% (scrub and grove unchanged). Decision to confirm | keep dense forest shut to trucks | `tree-trunks` |
| 448 | Clearing ground for a building, a bridge, an airfield piece or a tunnel (`World.ClearTrees`) takes away its trunks and every fallen trunk across a cleared cell, whole; a felled stump stays a stump while its cell keeps forest. Decision to confirm | clear only the fallen trunk's cells inside | `tree-trunks` |
| 449 | A unit whose goal a fallen trunk (or dense forest) shuts off goes as near as it can and its order ends, as for any unreachable goal; it does not wait for the trunk to go. **The owner accepted the defaults on 2026-10-25 (night run), still open to tuning: a blocked unit does not wait for the trunk to go** | keep the order and retry when the trunk expires | `tree-trunks` |
| 450 | Dense forest is shut to wheeled and light wheeled (`forestBlockedFrom` 3, was 4: they crawled through at 40%). Its trunks (21.25% of its cells; 17.5% at the first take) leave a car no way across (0 of 30 windows of 80 x 80 cells at both chances, see "The trunks") but winding pockets 2 cells wide that the clearance grid opens and a crowd of turning cars wedges in: in `SimRun -move-bench forest` on the 1,500 bench map, with the block lifted, 1% of 100 technicals arrived at 35 / 70 and 95% by tick 3,764 at 50 / 85 (the pockets close up); with it, 95% by tick 3,801 at 35 / 70 and 3,797 at 50 / 85 (95% by 4,870 before trunks). **The owner accepted the defaults on 2026-10-25 (night run), still open to tuning: dense forest is shut to cars** | open, with a lower dense trunk chance | `tree-trunks` |
| 451 | Blasts blow trees down by warhead: `trees.fell` in the terrain rules lists the warheads, each with a percent of its weapon's splash radius; every standing trunk whose point is within it falls. In the terrain rules so flat games and the golden replay are untouched; names checked against the content when the terrain is attached. Forest density stays. Decision to confirm | a field on each warhead; a table by airstrike tier | `tree-trunks` |
| 452 | `fellMinRadius` 15 (1.5 cells) for every felling blast: an anti-tank missile (no splash) or an RPG fells 1.0 tree on average in dense forest (0 to 3; 0.6 in a grove). Decision to confirm | 2 cells: about 2 trees | `tree-trunks` |
| 453 | `fell` 100 for rockets, missiles, shells, mines, the attack jet's heavy cannon and crashes; 60 for the drones' charges; 50 for the twin missile, the ballistic missile and every bomb (2.5 to 6.5 cells; at the first take's 35 / 70 a heavy bomb 20.6 trees in dense forest and a 20-bomb carpet 139, at 50 / 85 the carpet 172). Bullets, 30 mm cannon, autocannon, darts, air-to-air missiles and the anti-personnel mine fell nothing. Decision to confirm | bombs at 100 (a carpet clears a wood) | `tree-trunks` |
| 454 | A blown-down tree falls away from the impact (the 8-way direction from the impact to its trunk's point; north when exactly on it) and lies and goes as a pushed one; its event carries `Cause` Blast, the impact (`From`) and the shooter (`By`, 0 for mines and crashes). Decision to confirm | a random direction | `tree-trunks` |
| 455 | `fallDamage` 10 (percent of max HP) to units tagged `infantry` (`fallDamageTags`) standing on a fallen trunk's cells, once per trunk in ID order, pushed trees too; nobody inside a vehicle or building; vehicles none. Decision to confirm | damage vehicles; spare a soldier at 1 HP | `tree-trunks` |
| 456 | Felled cells open routes at once; sight does not change (it reads forest density, which only fire will lower: task C). Decision to confirm (owner question: the spec says sight lines change) | a felled cell sees as one density lower | `tree-trunks` |
| 457 | Carpet cost: a tick's falls are ordered once at its end; routing drops the sectors, regions and flows only of the classes whose costs changed (a fall is nothing to infantry); fog skips cells only a trunk changed; movement's wall distances redo the box round any number of changes. Carpet over dense forest p99 0.312 ms (0.269 without felling); before, 6.2 ms. No gameplay change. Decision to confirm | none | `tree-trunks` |
| 458 | Tunnel entrances clear trees only on their footprint and apron (`ShapeGround` calls `ClearTrees` on the cells of the pad, the door and the strip in front of it): a hatch 2 x 3 cells (4 x 6 m), a medium entrance 5 x 7 (10 x 14 m), a large one 7 x 10 (14 x 20 m), 6, 35 and 70 cells. Before, the whole shaped ground and a cell round it: 10 x 11 (20 x 22 m), 17 x 19 (34 x 38 m), 21 x 24 (42 x 48 m), 110, 323 and 504 cells. The trees on the blend ring stay standing (the ground still eases under them). No margin cell, unlike a building's one cell round. In a dense wood at the rules' trunk chance 70, facing south at (40, 40): 1, 7 and 13 trunks removed, against 19, 56 and 82. Decision to confirm | a one-cell margin round footprint and apron; the ring's inner cells as well; the old whole ground | `tunnels-forest` |
| 459 | The trees do not grow back: when the entrance is gone the ground steps back over 30 s and the pad flags come off, but the cleared cells keep forest 0 and no trunk (felled stumps stay stumps; nothing is stored per entrance). Decision to confirm | restore the forest of the cleared cells (stored per entrance and hashed), or let it regrow over minutes | `tunnels-forest` |
| 460 | A fallen trunk lying across a cleared cell is taken away whole, its part on the ring too (decision 448 applied to the smaller area); a fallen trunk lying on the ring alone stays and goes at its own time. Decision to confirm | cut only the cells inside | `tunnels-forest` |
| 461 | Placement in forest needs no new rule and no content change: a hatch and a medium entrance (`flatGrade` 0) go wherever their builders can walk, and the foot class enters every forest up to dense (`forestBlockedFrom` 4), so a foot worker places and builds them in dense forest (hatch 200 and medium 500 ticks of work plus the walk); the large keeps `flatGrade` 10, in forest as anywhere (refused on a 15% slope, allowed on flat ground in scrub, grove and dense). The only change is how much it clears (458). Decision to confirm | a forest rule (no large in dense forest; a free lane to the door) | `tunnels-forest` |
| 462 | Vehicles and a medium entrance in dense forest, documented not ruled: cars (wheeled and light wheeled, `forestBlockedFrom` 3) cannot enter dense forest, so the cleared strip in front of the entrance is a pocket for them. A car ordered to the door stops at the wood's edge with its Enter order standing; one that comes out stays on the strip (a Move order ends there, as for any unreachable goal: decision 449). Heavy trucks and tracked vehicles push trees over on the way (decisions 441 to 447): a cargo truck starting on open ground 29 cells from the door (14 of them through the wood) reached it in 542 ticks, felling 4 trees. In a grove (35% trunks) cars drive in and out between the trees. No refusal and no warning at placement: the player's choice (the client may show it). Decision to confirm | refuse a medium entrance cars cannot reach; clear a lane to the nearest open ground | `tunnels-forest` |
| 463 | The door point is one cell in front of the footprint, so on the hatch (apron 1) it sits on the outer edge of the cleared strip: a soldier comes out half a cell into the uncleared wood (infantry ignore trunks; nothing blocks). The medium's door is in the middle of its 2-cell apron, the large's 1 cell inside its 3. Decision to confirm (a content change: a 2-cell hatch apron changes the content hash and the golden) | hatch `apron` 2 | `tunnels-forest` |
| 464 | Concealment from the air applies only with fog of war on a map with terrain; without fog every unit is spotted by distance as before (ground sight ignores forest there too), so flat games and the golden replay play as before. Decision to confirm | apply without fog too | `concealment` |
| 465 | Canopy hides only units that move on the ground: infantry (`foot`) and vehicles (`wheeled`, `tracked`) out on the map. Structures (hidden tunnel entrances and stations included), traps, aircraft parked on the ground and units inside anything keep today's rules. Decision to confirm | hide structures too (a hatch under canopy; but U1 clears its footprint) | `concealment` |
| 466 | Cover is a density where any camera reach is limited (grove, dense); reaches are level distances from the point under the air unit, whatever its height (the Overseer at 250 m as the Orlan at 80 m), within its vision. Decision to confirm | slant distance; reaches by altitude | `concealment` |
| 467 | "Moving" is moving last tick (`Unit.Vel` not zero, pushes included): a unit nudged by a neighbour counts for that tick; stopping hides it at once (by the eye's next look). Decision to confirm | a speed threshold; a grace window after stopping; an ordered move | `concealment` |
| 468 | Vehicles: twice the infantry's reaches (grove 6 still / 16 moving, dense 8 moving) and still in dense forest within 4 cells (the owner's "4 or so"). Decision to confirm | 3 cells; dense still hidden for vehicles too | `concealment` |
| 469 | A shot reveals the shooter to every air sensor as in the open for the next 100 vision passes (`fireReveal`; any weapon, `Unit.LastShot`); not to ground units, not to every enemy (the howitzer's flash does that). Decision to confirm | by weapon (a rifle less than a cannon); to every enemy | `concealment` |
| 470 | `Stance.Hide` (5) behaves exactly as Hold fire in every system (no auto fire, no chase, no retaliation, air defence holds, orders obeyed); a stance of its own for the player's "hide" order and the client. Decision to confirm | an ambush (open fire once an enemy spots it); stop moving when given | `concealment` |
| 471 | Thermal's "engine ran" is moved within 600 ticks (`Unit.MovedAt`; firing or a turret turning does not warm it; a vehicle that never moved is cold); it sees as in the open; moving infantry in scrub and groves (`movingInfantry` true, true, true, false), never in dense forest, still infantry never. Decision to confirm | firing warms the engine; a crewed vehicle idles warm; a thermal reach shorter than vision | `concealment` |
| 472 | Ground radar is a sensor kind on the stealth multirole's heavy loadout (read as its "ground mode"); stealth mode has a camera only. It sees moving vehicles under any canopy within its vision (40 cells). Decision to confirm | both modes; its own reach | `concealment` |
| 473 | Sensors (owner default): Overseer, Watcher, gunship camera and thermal; the Orlan, the jets, the bombers, the helicopters, the FPV and attack drones a camera (the default when none are listed). Decision to confirm | thermal on the helicopters (an attack helicopter's FLIR) or the strike jets | `concealment` |
| 474 | A low helicopter is a rotary aircraft at most 40 m over the ground under it (`heliLow.height`: transit 30 m, gun run 15, hiding 5; popped up 60 m is not); it adds 2 cells to every limited camera reach (`heliLow.bonus`); hidden stays hidden. Decision to confirm | 1 cell; a percent; the bonus on dense still too | `concealment` |
| 475 | The attack helicopter's mast radar (`groundRadar`, vehicles only) and the H1 radar structures are unchanged: they spot vehicles under any canopy, parked or not. Decision to confirm | make the mast radar a ground radar sensor (moving vehicles only) | `concealment` |
| 476 | No engine last-seen table: an enemy lost under the trees drops out of `SpottedBy` and `SeenBy` the normal way, and the client keeps its last-seen marker (position and tick), as it keeps building ghosts. Decision to confirm | per-side last-seen positions in the engine (hashed) | `concealment` |
| 477 | The fog grid still shows the canopy as seen from the air (`FogGrid.Visible` counts air viewers); only spotting a unit standing on cover ignores them. Smoke screens are unchanged: an air unit's clear line still keeps a side's bit beside smoke. Decision to confirm | cover cells not visible from the air (woods grey under a drone) | `concealment` |
| 478 | The table is a top-level `concealment` block in `content/mvp` (a unit rule, like `unitAi`), not in the terrain rules (their digest is pinned by tests and would change maps' hashes); adding it and the sensors changed the content hash, so the golden replay was re-recorded for the content hash only. Decision to confirm | the terrain rules' vision block | `concealment` |
| 479 | A structure's turn is the turn of its archetype's layout, clockwise: quarter 0 as laid out (the front to the south), 1 front west, 2 front north, 3 front east; a command's facing 0, 8, 16, 24 is quarter x 8, the Build command's documented convention and the tunnels' (so facing 16, `Facing.South`, puts the front to the north). Decision to confirm | the facing as the direction of the front (16 as laid out): reverses the Build command's documented convention and the tunnels' | `building-rotation` |
| 480 | The turn is its own state, `Unit.Quarter` (hashed when not 0), not read from `Unit.Facing`: `World.Spawn(def, cell, facing)` keeps a structure as laid out whatever its facing, as before (scenarios spawn depots facing East or West, the sandbox spawned buildings facing South), and `World.Spawn(def, cell, facing, quarter)` turns one. Every old caller plays and hashes as before (a test pins a town's hashes from main 5f7a725). Decision to confirm | read the turn from `Facing` for every structure: old sandbox towns (facing South) would stand turned a half and every old caller would need checking | `building-rotation` |
| 481 | `SandboxSpawn` of a building reads `Command.Facing` as its turn (0, 8, 16, 24; 0, the default, as before); any other facing places nothing; its unit facing stays South. The client's town files are to pass a `facing` per building (job R1b). Decision to confirm | a new command field; snap other facings to the nearest quarter | `building-rotation` |
| 482 | Garrison doors no longer stop a turn (`World.CanTurn`: no production, no tunnel), so the observation post is built at all four facings. A post built at facing 16 now stands turned a half, its door (at its back, north, as laid out) to the south; before, facing 16 stood as laid out and East and West were refused. Decision to confirm | keep the post unturnable | `building-rotation` |
| 483 | Factories never turn: `CanTurn` refuses them (a site keeps quarter 0, and at facing 16 stands as laid out, as before) and the spawn API throws for a turned one. Their exits, bays, rally, exit lanes and service doors are laid out to the south. Decision to confirm | turn them too (the airfield pieces' `PiecePoint` for exits and bays, a turned exit lane and service doors) | `building-rotation` |
| 484 | A built site's quarter is its facing / 8 for every structure that may turn, square and door-less ones too (a supply depot at facing 8 is quarter 1: nothing in the sim changes but its hash, and the client may draw it turned). Games with a site built at a facing other than 0 hash differently from before; no stored replay has one. Decision to confirm | set the quarter only where something follows it (doors) | `building-rotation` |
| 485 | A factory's rally on a turned building goes to its front: the layout's cell (w / 2, h + 1), 2 cells out from the middle of the front edge; on an unturned one as before. Decision to confirm | always below it (south) | `building-rotation` |
| 486 | No new placement rule: a spawn does not check that a door's cell is open ground, turned or not, as before; the map generator keeps its own door check. Decision to confirm | refuse a spawn whose door cell is blocked | `building-rotation` |
| 487 | The long barn is added (the art landed): `farm_barn_long`, 15 x 4 cells (30 x 8 m), garrisonable like the farm shed, door `[7, 4]`, capacity 6, 900 HP, height 4.8 m from the art (see 707). Until a map places one, the cooperative's sheds stay warehouses. Decision to confirm | 16 x 5 cells (32 x 10 m); not garrisonable, like the hay store | `building-rotation` |
| 488 | The truck's cost, build time and HP are the spec's defaults: 1,600 supply, 50 s (1000 ticks), 280 HP; `medium_vehicle` armour, wheeled, speed 16 like the heavy rocket truck, a driver (`soldier`) crew like the other trucks, tags `vehicle`, `launcher`, `heavy`. Decision to confirm | 1,400 / 40 s like the twin missile truck; 300 HP; a slower truck | long-range rocket truck |
| 489 | Deploy 6 s (120 ticks) and stow 5 s (100) like the twin missile truck. Decision to confirm | 4 s and 3 s like the heavy rocket truck | long-range rocket truck |
| 490 | Glyph `X` for SimRun's text map: the only free capital among the Resistance's units (the Sentinel's watcher station uses it, another faction). Decision to confirm | any other letter; a two-character glyph | long-range rocket truck |
| 491 | Rocket numbers: 300 damage, 5-cell splash falling from 100% to 10% (the heavy rocket truck's profile), scatter growing from 4 cells at 50 to 6 at 130 (`scatter` 60, `scatterNear` 40), 18 cells/s (13 s of flight at full range), the heavy rocket warhead, the same arc as the other rocket trucks (none set). Decision to confirm | scatter flat 5; a faster or slower rocket; a lob like the tactical missile's | long-range rocket truck |
| 492 | Ripple of 10, one every 30 ticks, `reload` 20 like the other launchers. Decision to confirm | a different interval | long-range rocket truck |
| 493 | Reload 20 s (400 ticks) and 80 supply a rocket, only stowed, 20 ticks after the last shot (the other launchers' value): a full reload costs 800 supply (80% of a depot) and takes 200 s. Decision to confirm | cheaper rockets | long-range rocket truck |
| 494 | No single-shot fire mode: `fireModes` and `CommandKind.SetFireMode` exist only for aircraft bomb deliveries (`delivery.modes`); a ground launcher would need new engine code (a mode on a launcher weapon, the salvo shortened). Skipped as the owner allowed; the ripple is the only mode. Decision to confirm | an opt-in `modes` list on a launcher weapon (an engine change) | long-range rocket truck |
| 495 | In the Resistance faction list only (the vehicle garage builds every faction vehicle of its list), not in the golden battle's lobby: the golden's commands are unchanged and only the content hash (and the definition indices the world hash covers) moves. Decision to confirm | adding one to the battle lobby's armies | long-range rocket truck |
| 496 | The carrier's survival test lists the new weapon among the ones whose whole ripple (10 x 360, every rocket a direct hit) can kill an infantry carrier, as the heavy rocket truck's salvo does; a single rocket cannot (one rocket does 360 to it, under its 1,200 HP; ten make 3,600). Decision to confirm | a smaller ripple; fewer rockets | long-range rocket truck |
| 497 | `BuildingRotationTests`' old-town hashes were re-pinned: any new archetype in a faction shifts the definition index of every unit, which the world hash covers. Decision to confirm | none (pinned values change with any faction archetype) | long-range rocket truck |
| 498 | World.Hash keeps every original word, sign extension and ordering: four-word streaming rounds, a smaller per-unit hot loop, direct array/span reads for full paths and charges. No consumed path prefix is skipped, no state is cached; the original implementation remains an internal equivalence oracle. Golden unchanged. Decision to confirm | the original scalar loop; change the hash stream (rejected) | `2dfa1ed` |
| 499 | Distance vision lazily prunes per-viewer-side bucket candidates after their SpotUntil becomes the current deadline; state eligibility is fixed in the vision phase, and all pending references/flags reset at the next index build. Fog distance queries retain only Air-layer candidates before the unchanged InAir check. Target acquisition skips ordinary centre-to-centre candidates outside its existing maximum range bound before the unchanged exact weapon/priority rules. Index reuse remains limited to phases without movement. Scratch only; original indexed perception remains an internal oracle; golden unchanged. Decision to confirm | repeat already-satisfied distance candidates; share a stale index across movement (rejected) | `e5e08cb` |
| 500 | Fire burns per forest cell, not per trunk: a cell of scrub, grove or dense forest burns, its trunk (if any) with it; the cap counts cells. Scrub has no trunks but burns. Decision to confirm | per trunk (scrub could not burn; spread over gaps of 2 to 3 cells) | forest fire |
| 501 | A cell burns 400 to 800 ticks (20 to 40 s: the owner's range), rolled per cell; every fire roll (length, spread, a blast's count) is an integer hash of the match seed, the cell and the tick, never the world's generator, so fire never shifts another system's rolls. Decision to confirm | the world's `Rand` | forest fire |
| 502 | Spread with no wind: each burning cell looks once a second (staggered by cell) at its 8 neighbours and lights each with 6 / 40 / 80 per mille by the neighbour's density (scrub, grove, dense; open ground never), corners at 50%. Over a burn a side neighbour catches about 1 in 6 (scrub: a scrub fire mostly dies out), 7 in 10 (grove), 9 in 10 (dense). Decision to confirm | wind; 4 neighbours; other chances | forest fire |
| 503 | At most 150 cells burn at once on the whole map and spreading lights at most 6 a tick; at the cap nothing more catches (a blast's cells too) and the fronts wait, then go on as cells burn out. Decision to confirm | a cap per area; drop the per-tick cap | forest fire |
| 504 | A cell burns once: burnt out it glows 30 s (embers), then is ash, one forest level down; its standing trunk stays a black snag, still a trunk to every rule (a wall to the cars, pushed by tanks, felled by blasts). Decision to confirm | snags fall or vanish; a cell may burn again | forest fire |
| 505 | Ignition by the warhead's `fire` block (warheads.json): `bomb` lights 1 or 2 forest cells within its weapon's splash radius (standing trunks first, then the nearest), fires that never spread; shells, rockets and missiles have none. The gunship's 105 mm and big rounds use the `bomb` warhead, so they light 1 or 2 too; the cargo pallets (no splash) none. Decision to confirm | a warhead of their own for the gunship's rounds | forest fire |
| 506 | The hook for the incendiary (C3) and the hypersonic missile (H1): `radius` (tenths of a cell) and / or `radiusPercent` of the splash, `count` [fewest, most] (left out: every forest cell within it), `spreads`, `spreadPercent` (only the cells it lights; theirs spread at 100). Decision to confirm | a spread boost inherited by every generation | forest fire |
| 507 | Smoke: a burning cell not under a screen that lasts past its next look pops one round itself (4 cells, 20 s), at most 16 fire screens at once; they are the smoke system's screens (`Source` 0) with its sight and missile-lock rules. Decision to confirm | a screen per cell; thicker or longer smoke | forest fire |
| 508 | A soldier in a burning cell loses 10% of max HP a second and, unless already walking to a cell not burning, walks to the nearest one within 8 cells (its order replaced); a vehicle parked where fire catches drives out the same way, unhurt. Units inside vehicles and buildings are safe. Decision to confirm | vehicles hurt too; keep the order and only step aside | forest fire |
| 509 | Fire reaches the terrain in batches every 40 ticks (2 s): the cells lit become walls (`fireCost` 0: every vehicle class), the cells burnt out lose them and a forest level; routing re-plans once a batch. Infantry routes ignore fire (`fireCost` 100) and burnt or blasted cells keep their old forest cost and speed for them (`thinnedForestAsBefore`): with infantry walls and drops, every batch rebuilt infantry routing round the front, about 15 ms a batch on `-bench-fire 8`. Decision to confirm | infantry routes avoid fire (`fireCost` 400) at that cost; a shorter batch | forest fire |
| 510 | Units on their own route whose way ahead crosses a newly walled cell ask for a new route (a pass every 10 ticks, the fallen trunks' rule); a group re-plans its flows when routing sees the batch. Decision to confirm | wait for the stuck detection | forest fire |
| 511 | Decision 456's follow-up: a cell whose tree a blast blew down loses one forest level, once (`trees.blastThins`), so sight opens where a bomb or a shell felled trees; only those cells, not every cell in the blast; pushed trees thin nothing. Decision to confirm | every forest cell within the blast's fell radius | forest fire |
| 512 | Hashed: the burning cells, the burnt cells, the batch not yet applied and the blasted cells (`World.Hash` and the reference hasher). The terrain's fire bits are not; its digest reads a burnt or thinned cell's forest as it was, so fire never recomputes it (a pass over every cell). Decision to confirm | reset the digest on every change | forest fire |
| 513 | `CommandKind.SandboxIgnite` (43, sandbox only: scripted demos and tests) lights a cell; `StepPhase.Fire` is appended at the end of the enum (it runs right after the ground movement and tree step) so the numbers of the others stay. Decision to confirm | none | forest fire |
| 514 | Golden re-recorded for the content hash only (fire.json and the bomb's fire block): the golden battle is flat, no forest, nothing burns. Decision to confirm | none | forest fire |
| 515 | Speeds 125 cells/s cruise and 210 in the dive (the owner's 120 and 200): kept above every interceptor (the THAAD type's 120) and every other projectile (the gunship's light cannon, 200), as asked. Decision to confirm | the owner's 120 / 200 (a THAAD missile as fast as its cruise, the cannon round as fast as its dive) | hypersonic |
| 516 | The profile: a 1.5 s vertical climb to 40 m, the ballistic parabola at 40% levelled off at 150 m (new `terminal.cruise`), a straight dive over the last 30 cells. With the plain parabola and a 100-cell dive no Patriot or THAAD type at the target ever fused one in the trials (the arc too steep, the dive too long for a 1 s lock); levelled and short, both get shots. Decision to confirm | a higher cruise or a longer dive (fewer shots), a pure parabola | hypersonic |
| 517 | The weave: one wave of up to 1.5 cells over the 30-cell dive (about 3 ticks: draw it with `FlightPointAt`'s thousandths), its size (50 to 100%) and side seeded per shot from the match seed and projectile ID (no RNG draw, so no other roll moves); in the sim's position; 0 at the impact so it lands on its scattered point. Decision to confirm | a bigger swing, more waves, a longer dive | hypersonic |
| 518 | Unit targets allowed when seen (every launcher's rule); ground points fire without a spotter (already the launchers' rule). Decision to confirm | ground points only | hypersonic |
| 519 | The two missiles are a single-shot weapon with 2 rounds and an 80-tick reload (`salvoSize` 1), so the existing Shift queue of ground points gives each its own point; one point takes both. Decision to confirm | a salvo of 2 at one point | hypersonic |
| 520 | Damage on the `ballistic` warhead as asked; its forest fire through a new, generic weapon `fire` block (on top of the warhead's) instead of a new warhead (which every structure's `occupantWarheads` and the trees' `fell` table would have to list). Decision to confirm | a `hypersonic` warhead | hypersonic |
| 521 | Fire: `radius` 40 (4 cells), every forest cell in it, `spreads` true at 100% of the rules' chances. Decision to confirm | a count, a lower spreadPercent | hypersonic |
| 522 | Interception: class `hypersonic` (air class 20, appended), the Patriot type `killBest` 9 and the THAAD type 17 (the kill model's other terms bring them to about 5% and 15% a fuse: measured 4.2 to 6.2% and 15.4%), `speedRefs` 1400 for both (no speed penalty), no salvo. Adding the class changes no existing engagement: no other projectile carries it (`BotMatchMatchesGoldenCommands` passes). Decision to confirm | a salvo of 2 | hypersonic |
| 523 | Never once it dives (`World.Evades`): no new pick, and missiles already at it lose it and self-destruct; the climb can be engaged (only beside the truck). Decision to confirm | the climb immune too | hypersonic |
| 524 | The warning: 5 s, every player but the shooter's (the engine has no teams), derived from the closed form (no new state); a flight shorter than 5 s is warned at its launch; events `Launched`, `Warned`, `Diving`, `Struck`, `Intercepted`. Decision to confirm | only the players with units within 10 cells of the point | hypersonic |
| 525 | The reveal: the weapon's `reveal` 400 (generic, works on a vehicle), each launch starting the 20 s again. Decision to confirm | none | hypersonic |
| 526 | The truck: 4,000, 90 s, 500 HP, speed 14, radius 13 (heavy wheeled: pushes trees), set up 10 s, stow 8 s, a driver, tunnel size 3 (the heavy trucks'), glyph `\` (the last free printable glyph but `"`). Decision to confirm | other numbers | hypersonic |
| 527 | The cap: exclusive group `hypersonic`, `max` 1, no type lock (alive, queued or being built count; a lost one may be rebuilt). Decision to confirm | a type lock, 2 a player | hypersonic |
| 528 | Reload 90 s and 400 supply a missile at a depot, stowed, 1 s after the last shot (a full reload 800: 80% of a depot). Decision to confirm | cheaper, or a missile only from the garage | hypersonic |
| 529 | `FlightPointAt` takes thousandths of a tick: the closed form evaluated between ticks (exact at whole ticks), so the renderer draws the 3-tick weave smoothly. Decision to confirm | whole ticks only | hypersonic |
| 530 | In the Resistance list only, not in the golden battle's lobby and not used by the skirmish AI. Decision to confirm | AI use | hypersonic |
| 531 | Golden re-recorded for the content hash only (the commands are unchanged: no hypersonic missile flies in the golden battle); `BuildingRotationTests`' old-town hashes re-pinned (the content hash and the definition indices after the new archetype). Decision to confirm | none | hypersonic |
| 532 | The cruise height is over the launcher's ground and the dive is a straight line from the cruise height to the point's ground: a target on a hill is dived at less steeply, one in a valley more. Decision to confirm | over the point's ground | hypersonic |
| 533 | One incendiary bomb on each medium jet, **extra** (not a swap): the multirole with either bomb preset, the air superiority jet with its glide bombs, the stealth multirole in both modes (each loadout's `ammo`: 1). Decision to confirm | a swap for a regular bomb; two | incendiary |
| 534 | The switch is an upgrade: weapon `upgrade` "incendiary"; without it the stock holds 0 (`Unit.StockMax`), the jet otherwise unchanged. `Player.Upgrades` from the lobby's `upgrades` (none in any shipped lobby or the golden battle) and `World.GrantUpgrade` (the upgrade system's hook, C5), which also marks the player's units (`Unit.Upgrades`; a unit made later gets its owner's). A grant loads at the next rearm, not at once. Decision to confirm | load at once; a per-jet upgrade only | incendiary |
| 535 | `CommandKind.SandboxUpgrade` (44, appended; sandbox only: `Command.Unit` the upgrade's name) grants an upgrade for demos and tests. Decision to confirm | none | incendiary |
| 536 | Ground points only (weapon `pointsOnly`): never on an attack order at a unit or Guard's hunt. On the multirole and the air superiority jet it is the only bomb slot (selected): attack ground drops it first while aboard, then their other bombs; on the stealth multirole `SetBombWeapon` 1 picks it, its heavy bombs stay the default, a unit target or the incendiary spent brings the heavy bombs back. Decision to confirm | an explicit pick on every jet (a second slot) | incendiary |
| 537 | `SetBombWeapon` refuses a bomb weapon the jet does not carry (`Unit.Carries`: no upgrade). Decision to confirm | allow it, dropping nothing | incendiary |
| 538 | Damage as shares of max HP falling off with the splash (new warhead `maxHpBySplash`): within 4 cells (and a direct hit) infantry, light vehicles and parked aircraft 100%, `medium_vehicle` 70%, armour 40%; out to 7.5 cells 60% of that (60 / 42 / 24); buildings the weapon's 500 (300 outer). The cargo trucks are `light_vehicle`: destroyed in the centre. Decision to confirm | weapon damage through the table (HP-dependent); trucks destroyed too | incendiary |
| 539 | The incendiary's share ignores directional armour (fire wraps a hull): a tank loses 40% wherever it faces (with it, a jet's blast counts as a top hit, 150%: 60%). Decision to confirm | keep directional armour | incendiary |
| 540 | Its fire block: every forest cell within 6 cells (about 113, inside the 7.5-cell blast and under the 150 cap), `spreadPercent` 300; open ground within it burns 12 s (new block field `ground`, ticks, fixed per cell). Decision to confirm | 7.5 cells (about 177: over the cap); 10 or 15 s | incendiary |
| 541 | Cells lit by spreading keep their source's spread percent (decision 503 lit them at 100): an incendiary's front stays at 300; fires at 100 (every other) spread exactly as before. Measured over scrub: 436 cells burnt against 131 at 100; over dense pine both run at the cap. Decision to confirm | only its first ring at 300 (C1 as built) | incendiary |
| 542 | A ground fire: open land without forest (roads too), not water, a deck or a building; it hurts infantry, walls vehicles, smokes and lights the forest beside it as a forest fire does, counts under the cap (lit after the trunks, nearest first), and burns once: scorched ground (burnt, then ash), no forest level to drop. Decision to confirm | ground that can burn again; no smoke from it | incendiary |
| 543 | The bomb: 500 damage (tier 3), 1 a jet, the multirole's 35-degree dive delivery and scatter, mode `single`, a small runway crater, a station's rearm only; it fells no trees (not in `trees.fell`). Decision to confirm | the 2,000 lb's look (tier 6); felling | incendiary |
| 544 | Hashed: `Player.Upgrades` and `Unit.Upgrades` when not 0 (both hashers); the setup digest has `upgrades` only when present; the new content fields hash only when set. Ground fires are `World.Fires` entries. Decision to confirm | none | incendiary |
| 545 | Golden re-recorded for the content hash only (a new warhead and component; the jets' new stock is empty: no jet in the golden battle); `BuildingRotationTests`' old-town hashes re-pinned for the content hash. Decision to confirm | none | incendiary |
| 546 | Perf: `SimRun -bench-incendiary 4` (4 incendiaries over 1,024 x 1,024 cells of dense forest, 300 units walking, fog): fire system p99 0.006 ms, max 0.035; tick p99 0.52 against 0.34 ms without (routing round the walls). Decision to confirm | none | incendiary |
| 547 | Perf: `RunGarrisons` counts every building's occupants in one pass (the live occupants' building IDs sorted and walked beside the buildings in ID order) instead of a scan of every unit for every building; the same counts, so no result change (bench hashes and the golden unchanged; `GarrisonIndexTests` against `World.FullGarrisonScan`). Zuberec's convoy tick p50 0.83 -> 0.27 ms Release, 5.75 -> 1.21 ms Debug | an occupant count kept on the building (state to hash and keep in step) | zuberec-perf |
| 548 | `SimRun -zuberec-bench` makes the world as the client does (`SimSession.Start`, `Town.Prepare`: pads, forest cleared, the Sentinel lobby trimmed to a rifleman each, a sandbox, `PrepareRouting(everySector: false)`, every building a neutral `SandboxSpawn` of player 2 at its turn), and reads the map and town from the game (they are not copied here) | a synthetic town | zuberec-perf |
| 549 | Not done: the town's other ~0.2 ms a tick (Release; ~0.85 ms Debug), mostly the spatial index rebuilt twice a tick (vision, combat) with every building filed under every bucket its footprint touches, and the systems' linear passes over 1,000 more units. A persistent layer for structures would have to keep every bucket's ID order exactly | an incremental index | zuberec-perf |
| 550 | The client's bench measures the sim in a Debug build (`client/bench/run.sh` builds `Client.csproj` without `-c Release`): about 4 to 7 times slower than Release on these scenes, which matches its ~4 ms tick frames on Zuberec better than Release's 0.83 ms. Decision to confirm (the client: bench the sim in Release) | none | zuberec-perf |
| 551 | Launchers are built freely: any number set up within 100 cells of a live, set-up S-400 type radar join its battery (the owner's open question 1; the coordinator's default). Decision to confirm | two launchers a battery | s400-engine |
| 552 | Aircraft only (open questions 2 and 3; the coordinator's default): classes multirole, fighter, attacker, gunship, bomber, stealth, airlifter and the three helicopter classes; not ballistic, missile, rocket or hypersonic, and not the drones (scout, Watcher, Overseer, FPV, Kaman). Decision to confirm | missiles too (as the Patriot type); the drones | s400-engine |
| 553 | Counter-stealth is one archetype field on the sensor (`counterStealth`); a battery launcher locks and kills by the highest counter-stealth among the archetypes carrying its `radarTags` (worked out from content, no field on the missile), so only its own radar's number counts. Decision to confirm | a `counterStealth` on the interceptor as well | s400-engine |
| 554 | The radar's cue (`cueLock` 70, spec) also shortens its own launchers' lock, as the mobile radar's does the SA-6's: 3 s x lock value x 70% (2.1 s on a normal target, 4.2 s on the stealth bomber). Decision to confirm | no cue on its own launchers (3 s and 6 s) | s400-engine |
| 555 | The radar horizon: level distance past 200 cells; the target's height over the ground under it below 50 m; the line from the antenna (25 m) to the target sampled every half cell (`LineOfSight`); a flat map hides nothing (no curvature of the earth). It counts for the side's tracks, the fog, the battery link and the semi-active guidance. Decision to confirm | the curvature of the earth as well | s400-engine |
| 556 | Who picks up the emitter: every working enemy unit with an `airVision` or a `groundRadar`, not tagged `infantry` (`excludeTags`: an AA soldier's eyes are no radar): the jets, the bombers and the airlifter (their short air visions count), the radar truck, the base radar, the radar site, the attack helicopter's mast radar; within the emitter's 800 cells (slant from its antenna to the sensor's eye), whatever the sensor's own reach. Decision to confirm | a tag list of radar sensors; the sensor's own reach | s400-engine |
| 557 | The reveal is renewed on each sweep (every 80 ticks, phased by ID like `RadarSweep`) and lingers 200 ticks after it, so it stays on while the radar searches; it first shows up to 4 s after the radar is set up. It reuses `RadarSpot` (one per radar and player; `Spotter` the lowest ID sensor that picked it up; `Inside` true on the renewing sweep). Decision to confirm | renewed every tick | s400-engine |
| 558 | The Hide stance switches an emitter's radar off (no search, cue, battery or emission; the array stops: `RadarSweep` -1); `SetStance` is now accepted by an emitter that has no weapon (it was refused for every unit without one). Decision to confirm | Stow only | s400-engine |
| 559 | Kill model: `killMax` and `killTail` 85, `killHead` 74 (the SA-6's 80 / 92 ratio), `killBest` 85 for the jets, bombers, gunship and airlifter, 70 for the helicopters; the SA-6's `speedRefs`, `crossHalf`, `speedRef`, `rangeFar`, `trackBonus`, `trackFull`, `killMin`, `leadTicks`, a 2-cell fuse, a turn rate of 60 and a 3 m launch height. Decision to confirm | the SA-6's 92 / 80 tail and head | s400-engine |
| 560 | Hit damage: 35% of max HP a hit for the bomber and stealth classes (spec) and for the gunship and the airlifter (counted as heavies); every jet, the attack jet included, and every helicopter goes down to one hit (a helicopter then goes out of control, not outright). On the stealth class 35% keeps its 3 hits (the other interceptors take twice the bomber share there; `StealthBomberTests` allows either). Decision to confirm | the SA-6's per-class shares for the gunship, airlifter and helicopters | s400-engine |
| 561 | Salvo 2 at the `bomber` and `stealth` classes (the strategic, supersonic and stealth bombers), 1 at the gunship, the airlifter and everything else. Decision to confirm | 2 at every heavy | s400-engine |
| 562 | Tags: the radar carries `s400_radar` (the launchers' `radarTags`) and `air_radar` (the stealth multirole's radar spotting reveals it), not `radar`, so the SA-6 launcher does not link to it and the S-400 type launcher not to the mobile radar; both parts carry `heavy` (8x8 trucks, as the hypersonic truck). Decision to confirm | one battery type for both radars | s400-engine |
| 563 | (superseded by row 623: `verticalLaunch`, it never turns) The vertical launch is the client's: the sim turns a launcher turret (`turret` true, the usual rate: aligned in a few ticks, inside the 3 s lock). Decision to confirm | a field for a launcher that never turns | s400-engine |
| 564 | Chassis numbers from the hypersonic truck (radius 1.3 cells, turn rate 1, `accelTicks` 40), 6 cells of sight, a driver; the launcher's 2,500 split as 2,100 + 400 for the missiles (one missile's reload cost, the SA-6's split). Decision to confirm | none | s400-engine |
| 565 | The antenna's 25 m (spec) is the radar's `height` 250: its sensor's eye, and also its body height for what flies down at it (a drone's dive stops 25 m up over it). Decision to confirm | a separate antenna height field | s400-engine |
| 566 | Weapon `reveal` now also flashes on an air defence launch (`NoteShot` in `LaunchInterceptor`); no other interceptor has a `reveal`, so nothing else changes. Decision to confirm | none | s400-engine |
| 567 | Hashing: no new state (the reveals are `RadarSpot`s, hashed in both hashers; the horizon checks and each battery's counter-stealth are scratch); the content hash has the new fields only when set. Golden re-recorded for the content hash only (two archetypes and a component; neither in the golden battle); `BuildingRotationTests`' old-town hashes re-pinned for the content hash and the shifted definition indices. Decision to confirm | none | s400-engine |
| 568 | Decision 194 (no sensor sees the stealth bomber in flight at 700 m) no longer holds for this one sensor: the S-400 type radar tracks it out to about 496 cells of slant (about 350 cells over the ground), and its battery engages it (spec). Decision to confirm | none | s400-engine |
| 569 | Not built: the tier 2 doctrine pick (picks are their own job), a build limit (spec: none for now), skirmish AI use, anti-radiation weapons homing on the emitter. Decision to confirm | none | s400-engine |
| 570 | Four missiles a truck (the owner's open question 1; the coordinator's default). Decision to confirm | two | cruise-engine |
| 571 | Only ground air defence shoots it (open question 2; the owner's default): no air-to-air weapon lists the `cruise` class, so the jets neither lock nor gun it. Backlog: fighters against tracked cruise missiles (an air-to-missile target). Decision to confirm | fighters with guns and missiles once it is tracked | cruise-engine |
| 572 | One route a missile (open question 3; the default): the route rides on `AttackGround` (`Command.Route`, written in replays only when set); Shift queues the next missile's route; a truck holds up to 17 (the ground queue's 16 after the first), and routes beyond its missiles wait for a reload. Decision to confirm | one route shared by all | cruise-engine |
| 573 | The slope rule is this truck's only (open question 4; the default), as a generic archetype field (`deploySlope`); its footprint is every cell the square of its radius touches, by `TerrainMap.Grade` (the largest corner difference over a cell); Deploy is refused (`Slope`), the truck is not driven off; an attack-ground order drives it to a spot within 4 cells as before. Backlog: the other launchers (the rocket and missile trucks, the S-400 type). Decision to confirm | every launcher | cruise-engine |
| 574 | A new air class `cruise` (value 21), not `missile` as the spec says: the Patriot type gets its `missile` best (85) on it, but a class of its own lets the air defence vehicle and the Igla treat it as a drone without also engaging the twin missile truck's arcing missiles. Decision to confirm | the `missile` class, and the short-range weapons engaging every arcing missile | cruise-engine |
| 575 | Kill terms: the Patriot type's speed reference for the class is 40 cells/s (its `missile` one, 15, would cut 85 to about 36% at 35 cells/s), THAAD's the same with its `missile` best (70); the air defence vehicle and the Igla take no class terms (their drone terms: best `killMax`, speed reference 12 cells/s, so about a third of their chance at 35 cells/s). Like an arcing missile, the Patriot and THAAD types engage one only when its point is within their 15-cell defended radius of their side (`defendRadius`). Decision to confirm | a speed reference for the short-range weapons too; the Patriot type defending wherever it flies | cruise-engine |
| 576 | The low-flight rule (generic weapon block `lowFlight`): "ground or vehicle sensors" are every sensor that is not an aircraft (soldiers' eyes, vehicles, radar trucks, structures); the clear line is the straight line from the sensor's eye to the missile over the ground plus the canopy of each cell's forest density (the fog's 0 / 2 / 6 / 10 m), every half cell; 120 cells of level distance; it counts for team cueing, the interceptors' own sight and the radar-linked and battery launchers. Decision to confirm | the fog's sight lines instead | cruise-engine |
| 577 | A projectile signature (generic weapon field `signature`, the archetypes' detect / lock / kill): 40 / 150 / 100 (spec), so a radar truck sees it at 144 cells when the low-flight rule does not hide it, and a lock takes 1.5 times as long. Decision to confirm | none | cruise-engine |
| 578 | The warning: an enemy player is warned while his sensors see it and for 5 s (`warn` 100) after they last did; `IncomingStrikes` lists it for those players only (`Warned`), `IncomingEvents` gets a new `Seen` (appended) on each player's first sighting, and `Launched` carries no warned players. Decision to confirm | one 5 s warning from the first sighting | cruise-engine |
| 579 | Exposure in the boost: the boost to 40 m is above the 30 m line, so a sensor within its normal reach of the launch point (144 cells for a radar truck) sees every launch, valley or hilltop; a valley launch stays hidden from sensors beyond that (and beyond 120 cells or behind cover once low). Decision to confirm | the boost exempt from exposure | cruise-engine |
| 580 | The flight is planned at the launch (a track of positions and heights a tick), not a closed form of the age: `FlightPointAt` reads it; the plan is hashed as a digest of its route and track. A missile in flight does not see later terrain changes (a crater, a levelled field, a burnt forest). Decision to confirm | none | cruise-engine |
| 581 | The height's surface is the ground (the water's surface over water) plus the treetops, sampled every half cell along the path; 15 m over the highest within the next 10 cells; the pop-up 40 m over the surface where the dive starts, its climb within the 20 m/s; the final dive (4 cells) is the one part exempt from the 15 m/s descent. Decision to confirm | the pop-up exempt from the climb limit | cruise-engine |
| 582 | What is refused: only a planned path that meets the ground (trees do not stop it) at the limits, by `CheckCruiseRoute`'s leg; a leg where it cannot keep 15 m (climbing out of a valley) flies lower for a while. Since climbs start early, in practice only a wall within reach of the launch point fails; the last cell before the point is not checked (the dive's end). Decision to confirm | refuse a leg below 15 m too | cruise-engine |
| 583 | Turns: an arc tangent to both legs (6 cells), tighter where a leg is short (the tangent point at most half a leg from its waypoint); a turn back within about 10 degrees of a reversal is a sharp corner. The leg into a waypoint ends at its arc's middle (`CheckCruiseRoute`'s numbering, 0 from the truck); the route's length is its straight legs (the HUD's), the flown path a little shorter. Decision to confirm | refuse a turn tighter than 6 cells | cruise-engine |
| 584 | The weapon's range is 2,000 cells (the route maximum) so the truck never drives to fire, and it has no minimum range (the route's 150 is the band); the hypersonic truck stays the longest straight shot (`HypersonicTests` skips cruise weapons there). Decision to confirm | none | cruise-engine |
| 585 | Chassis numbers from the hypersonic truck (radius 1.3 cells, heavy wheeled, turn rate 1, `accelTicks` 40, crusher 2, 5 cells of sight, a driver), tags `vehicle`, `launcher`, `heavy`, glyph `j`; no exclusive group (spec: no limit for now). Decision to confirm | none | cruise-engine |
| 586 | The warhead's extras: no scatter, a large runway crater (every ground weapon of 250 and more declares one; the ballistic and hypersonic missiles' size), no fire and no felling beyond the `heavy_missile` warhead's own. Decision to confirm | a small crater | cruise-engine |
| 587 | Ground points only: an Attack order on a unit and auto targets never fire it (`PickWeapon` skips a cruise weapon); with no route given an attack-ground order flies straight to its point. Decision to confirm | none | cruise-engine |
| 588 | Seen means by a sensor: a unit with an `airVision` (as team cueing). Vision of the ground (`vision`) never sees a missile; nothing here touches the fog. Decision to confirm | none | cruise-engine |
| 589 | Hashing: the missile's age, height, plan digest, sightings and warnings and the truck's routes, in `World.Hash` and `HashReference`; content hash only when the new fields are set. Golden re-recorded for the content hash only (the new archetype, component and air class, the class added to four interceptors; none in the golden battle). Decision to confirm | none | cruise-engine |
| 590 | Not built: the tier 1 doctrine pick (picks are their own job), skirmish AI use, fighters against it (row 571), the slope rule for other launchers (row 573), the client part (the route drawing, the booster drop and wings, the warning marker, `--demo=cruise`). Decision to confirm | none | cruise-engine |
| 591 | H4: one smooth arc (rise, fall, dive; slope continuous) replaces the 1.5 s vertical climb, the level cruise at 150 m and the straight dive, which the owner saw as straight lines. A closed form of the way along, so `FlightPointAt`, `ImpactTickOf`, `TerminalPhaseOf` and `IncomingStrikes` keep their signatures and meaning. Decision to confirm | a ballistic parabola (launch angle about 22 degrees only), a boost-glide profile | hyper-arc |
| 592 | The peak: a tenth of the ground range (`peak` 10) over the higher of the launcher and its point, at least 100 m (`peakMin` 1000: above the 72 m separation point, which the compiler requires) and at most 250 m (`peakMax` 2500): 100 m at 200 cells, 160 m at 800, 250 m at 1,500. Decision to confirm | no floor (the dive shortened on short shots), a higher cap | hyper-arc |
| 593 | Where the peak is: a fifth of the way to the dive's start (`peakAt` 20), the rise a cubic easing out (three times the rise over its length): about 52 degrees off the truck at 1,500 cells, 57 at 800, 77 at 200, 81 at 150; the fall a parabola. Decision to confirm | a peak further along (shallower launch), a fixed launch angle | hyper-arc |
| 594 | The terminal dive: the last 30 cells as before (`from` 300) from a 72 m separation point (`slope` 1200, a 50-degree chord), a parabola from the fall's slope (about 8 degrees) ending at about 66 degrees (the old straight dive was 68), with the old weave (1.5 cells, one wave) and the old 210 cells/s. Decision to confirm | a straight dive (a corner at the separation point), a longer dive | hyper-arc |
| 595 | Speed: 125 cells/s over the ground from the first tick (more in space on the steep rise), 210 in the dive; the old climb's 30 ticks are gone, so every flight is 1.5 s shorter (1,500 cells 11.9 s instead of 13.45). Decision to confirm | the old flight time (a slower start) | hyper-arc |
| 596 | Phases: `Climb` is still the first `climbTicks` (30) ticks, now up the arc (the client eases its tube offset over them); `Cruise` the rest of the arc; `Dive` over the last 30 cells, first if a short shot reaches it within the climb. Engageable in `Climb` and `Cruise`, never in `Dive` (`World.Evades`, unchanged). Decision to confirm | `Climb` up to the peak | hyper-arc |
| 597 | Content: `terminal.climb` and `terminal.cruise` are gone; new `peak`, `peakMin`, `peakMax`, `peakAt`, `slope` (all required). `TerminalFlight` loses `ClimbRaw` and `CruiseRaw` and gains `PeakPercent`, `PeakMinRaw`, `PeakMaxRaw`, `PeakAtPercent`, `DiveSlopePermille` (the client reads only `ClimbTicks` and `DiveSpeed`, kept). `Projectile.ArcRise` holds the peak's absolute height for this profile. Decision to confirm | none | hyper-arc |
| 598 | The Patriot type's `killBest` against `hypersonic` 9 to 6: on the higher arc the kill model's terms gave 7.5% a fuse; 6 gives 5.0%, the 5% of row 522. The THAAD type kept at 17 (12.8 to 13.4% a fuse, inside its about 15%), but it now often fires its second shot after a miss (`maxPerTarget` 2), so it brings down about 17% of the missiles at it (31 in 180) instead of about 8% (14 in 180). Decision to confirm | THAAD `killBest` about 9 (the old share of missiles brought down) | hyper-arc |
| 599 | HYP-INT: working as designed, not a bug. The Patriot and THAAD types engage an arcing threat (the hypersonic missile included) only when its point is within `defendRadius` (15 cells) of a unit or structure of their side (`Defended` and `Threatened` in `Sim/Systems/AirDefenseSystem.cs`). In the client's `--demo=hypersonic` truck B aims at (1160, 840) with nothing of red's within 15 cells (the battery is about 160 cells short of it, the squad about 90 cells north): 0 interceptors on seeds 2 to 5 and 42, as `TheDemoBatteryDefendsOnlyWhatIsNearThePoint` reproduces (flat, the demo's geometry shifted); with one red rifleman at the point, launches on every seed. The client demo should aim truck B within 15 cells of red units (e.g. red's units or the battery's radar) or put red units at its point; the battery then also engages at the point rather than 160 cells short. Decision to confirm | a `defendRadius` exemption for the hypersonic class | hyper-arc |
| 600 | Golden re-recorded for the content hash only (the commands unchanged: no hypersonic missile flies in the golden battle). Decision to confirm | none | hyper-arc |
| 601 | S1's model: one path a squad, walked by a virtual **anchor** (the leader's slot) at the squad's speed; every member, the leader too, steers to his slot round it. The leader is not walked along the path himself, so a leader who stops to shoot or is pushed aside does not drag the shape with him. Decision to confirm | the leader walks the path and the others follow him | squads-s1 |
| 602 | March slots lie along the anchor's **trail** (its walked points, half a cell apart, as many as 10 x 3 cells needs): the file walks in the leader's footsteps and bends round corners. Before the trail is long enough a slot goes straight back the way the trail came (just formed: behind the leader's facing), so a file formed facing north and sent east first swings round behind him. Decision to confirm | a straight file behind the facing (it cuts corners) | squads-s1 |
| 603 | March order from the front: recon 5 cells ahead along the path (more recons 1.5 cells further each), the leader, the flanks, the line with the medics put in its middle (after the leader at least), then long flanks, air guards (3 cells behind one another) and support (1 cell). Decision to confirm | the medic in the middle of the whole file, rear included | squads-s1 |
| 604 | Assault places: the leader in the middle of the line, line soldiers then flanks each on the shorter side (the right first), so the flanks take the ends; behind it the medic 3 cells, long flank 4, air guards 5 back 3 apart, support 7 back 1 apart (each role spread round the middle, the right first), recon 4 back 2 cells beyond the line's right end (a second recon beyond the left end). The spec's "2 to 4 cells behind" taken as 3; "behind, spread" as 5 back 3 apart. Decision to confirm | other depths | squads-s1 |
| 605 | Speed: the slowest member's speed times the mode's percent (100, 80), times the terrain percent under the anchor for the leader's class. Members walk at their own top speed to their slots, so only the slowest can't make up ground; the regroup wait covers him. Decision to confirm | the slowest member's terrain, not the leader's | squads-s1 |
| 606 | Regroup: a moving squad waits while a following member is more than 4 cells from his slot, for at most 60 ticks in a row, then walks on until all are within 4 cells again (the count starts over). Just formed and sent off at once, a squad spends its first seconds forming up. Decision to confirm | no limit (wait for the last man), a slower pace instead of a stop | squads-s1 |
| 607 | Steering to the slot: straight (the movement system's ordinary intent, own top speed, braking onto the slot) when the slot's cell fits the member's class and the straight line there is clear (`Router.Clear`); otherwise a route of his own to the slot's cell, or to the anchor's when the slot's cell does not fit, re-planned at most once a second. Squad paths are planned in the squad pass, at most 64 a tick (the path system's request budget; the rest wait a tick); the first plans over unbuilt sectors pay for them as group moves do (50 squads ordered at once on a cold 256-cell map: one 24 ms tick, the same soldiers in plain group moves 22 ms). Decision to confirm | a flow field a squad | squads-s1 |
| 608 | The squad's order ends when the anchor reaches the end of its path; members still walking to their slots are idle by then (`State` is Idle). Decision to confirm | Moving until every member is on his slot | squads-s1 |
| 609 | Form Squad with no squad among the listed: chunks of 10 in ID order, a last chunk smaller than 2 stays loose. With several squads over 10 together the whole command is refused (`MergeRefused`): the loose soldiers listed with them do not join either. Forming stops every member (his order is dropped) and the squad takes its leader's stance. Decision to confirm | the loose soldiers join the lowest-ID squad anyway; the most common stance | squads-s1 |
| 610 | Detach drops only the squad's move from the soldier (he stops where he is); an order of his own goes on. Disband stops every member's squad move, in place. Merging drops the merged squads' moves; soldiers who join a moving squad stop and fall in. Decision to confirm | none | squads-s1 |
| 611 | Orders widened to the whole squad: Move, Attack-move and Set stance (the squad's); Attack, Attack ground, Guard, Scatter, Enter, Exit and Stop make it loose until every member on the map is idle again, then it rebuilds round the leader where he stands. So Scatter scatters the squad and it then gathers again, Guard holds every member on his own post until a squad move, Stop gathers the squad round its leader. Abilities, Deploy, Stow and the rest stay one soldier's. Decision to confirm | Scatter, Guard and Stop as squad orders of their own (S2's Hold may replace Guard and Stop) | squads-s1 |
| 612 | Exit: every member in the same building, or riding in the same vehicle as a passenger; a member at the wheel stays. A squad stays a squad while all its members are inside; when they come out (an Exit or ejected) it rebuilds round its leader, or round the first member on the map while the leader is still inside. Decision to confirm | none | squads-s1 |
| 613 | Several squads in one move: side by side across the move, 12 cells apart, centred on the clicked cell, in ID order; the direction from the squads' anchors' centre to the cell. Loose units in the same order go on the ordinary group move to the clicked cell. Decision to confirm | in a column, one behind the other | squads-s1 |
| 614 | Leash: a member's leash point and walk-back point are his slot as it is now (`HomeOf`); a Guard order keeps its post. On a squad Attack-move the squad waits while a member chases or stops to shoot, so the slot holds still during a fight; on a squad Move members do not retaliate (as on any Move). Decision to confirm | the slot where the chase began | squads-s1 |
| 615 | Roles by class tags: four new tags, `medic` (`sentinel_medic`), `rpg` (`anti_armor`: the Tank hunter and the Drone hunter alike), `atgm` (`kornet`), `manpads` (`manpads`); `sniper` and `mortar` existed. The engineer is Line. A Resistance medic (separate task) only needs the `medic` tag. Decision to confirm | the Drone hunter as an air guard (needs a tag on its preset: presets have none) | squads-s1 |
| 616 | A squad of only medics, snipers and mortars is led by its lowest ID. Decision to confirm | none | squads-s1 |
| 617 | A deployed member (the mortar set up) does not follow and does not hold up the regroup wait; Move orders still reach him as for any unit. S2 sets the mortar up in Hold. Decision to confirm | the squad packs him up to move | squads-s1 |
| 618 | Hashing: the squads in `World.Hash` and `HashReference` once a squad has been formed (`_nextSquad`): id, owner, leader, mode, stance, order, goal, anchor, facing, path, route, trail, wait, loose, members. The slots (derived from those, the roles and the rules) and the per-tick flags (`Unit.SquadFollow`, `SquadSlot`, `SquadHalt`: written and read within a tick) are not hashed. New `StepPhase.Squads` appended. Decision to confirm | none | squads-s1 |
| 619 | Golden re-recorded for the content hash only (the `squad` block and the four tags; `BotMatchMatchesGoldenCommands` passes: the bots form no squads). Decision to confirm | none | squads-s1 |
| 620 | Not built in S1: Hold, Ambush and the air ambush, position picking and the role weights (S2 to S4), the computer opponent's squads (S5), the Resistance medic (separate task), selecting a whole squad by clicking a member and the squad card (client), `--demo=squads` (client). Decision to confirm | none | squads-s1 |
| 621 | L-YAW-E: the truck launchers' box is fixed on the hull (archetype `hullAims`, opt-in data; no `turret`). Stowed with an attack or attack-ground order in a firing position, the truck turns in place to face what it fires at and sets up only once it faces it (exactly, to the facing); set up it never turns. Decision to confirm | a small traverse of the box while set up (a few degrees, as many real launchers have) | hull-aim |
| 622 | The arc (`hullArc`): degrees either side rounded down to whole facings of 11.25 degrees, at least one; the default is one facing, the fire alignment every weapon already had (the brief's "about 10 degrees" at the sim's 32-facing resolution). Decision to confirm | a finer facing resolution for launchers; 10 degrees exactly | hull-aim |
| 623 | Vertical launchers (`verticalLaunch`): the S-400 type launcher (canisters raised vertical) and the hypersonic truck (tubes raised to the vertical; its arc leaves steeply along the line to its point, decision 591) never turn, hull or box. Supersedes decision 563 ("the sim turns a launcher turret"). Decision to confirm | the hypersonic truck aims with its hull (its arc leaves at 52 to 77 degrees, not straight up) | hull-aim |
| 624 | A set-up hull-aiming truck whose target is off its arc between salvos (a new order, the next Shift-queued point, a unit target that drove round) packs up, turns and sets up again: stow + turn + set-up (the rocket jeep 5 s, the twin missile and long-range rocket trucks 11 s, the tactical missile truck 16 s; the turret took 0.2 s). Never during a salvo (its rounds fly to the point fixed when it began). Decision to confirm | refuse the order; keep a turret for the re-aim only; a traverse arc (621) | hull-aim |
| 625 | The drone truck and the cruise truck take `hullArc` 180 (any way): their drones and missiles turn on their own after the rail, so the truck faces its first point before setting up and then fires at every later point (the cruise truck's four routes, 2 s apart; the drone truck's second point 3 s after the first) without packing up. Decision to confirm | arc one facing (a set-up between differently aimed rounds) | hull-aim |
| 626 | The tactical missile truck aims with its hull (an erector, arc one facing; it fires one missile, so only its set-up turn counts). The Kaman truck aims with its hull, arc one facing (one drone on a long rail). Decision to confirm | the tactical missile truck as a vertical launcher (a real erector rises to the vertical) | hull-aim |
| 627 | The air defence vehicle (`sam_vehicle`) loses its turret: the hull turns to its air target at its turn rate, 2 facings a tick, the old turret's rate; it engages only standing, so it fires when it did (tick 159 in the `SamVehicleTests` engagement facing the drone, away from it or across). No set-up, so no `hullAims`. Decision to confirm | keep the Osa type's turret (the client then draws a turning box) | hull-aim |
| 628 | Unchanged: the Patriot and THAAD types (no turret already; radar-linked, they fire from their fixed box and the missile turns), the light and medium rocket trucks (no turret, no set-up: the hull turns before each salvo, the fixed-weapon rule), the SA-6 type launcher (its turntable, not a truck box). Decision to confirm | the SA-6 type aims with its hull too | hull-aim |
| 629 | The turn is in place at the archetype's `turnRate` (facings a tick: a half turn 16 ticks at 1, 8 at 2), no movement and no three-point turn; a stowed truck pays at most 8 ticks more than the turret did (the turn comes before the set-up, not after). Decision to confirm | a drive-round turn (a short loop) for long trucks | hull-aim |
| 630 | Hashing: no new state (`Facing`, `TurretFacing` and the deploy state were hashed); the three archetype fields are in the content hash (block `launcherAim`, written only for units that have one). Decision to confirm | none | hull-aim |
| 631 | Golden re-recorded: the battle's 30-tube rocket trucks (`heavy_rocket_launcher`, one a side) now turn their hulls, and the bots' commands differ from tick 2,042 (240 commands before, 412 now). Checked: with that one unit kept on a turret the recorded commands equal the old 240 exactly, so its hull turn is the only gameplay change in the golden battle. Decision to confirm | none | hull-aim |
| 632 | Tests changed with the rule: `SamVehicleTests` expects the hull to turn (renamed `TheHullTurnsToTheTargetAndLaunchesFromItsRails`); `PatriotTests`' firing truck is spawned facing the battery (a seeded test whose tactical missile left 16 ticks later facing away); `BuildingRotationTests`' old-town hashes re-pinned for the content hash (no launcher in that town). Not built: the skirmish AI does not turn its launchers ahead; no traverse arc. Decision to confirm | none | hull-aim |
| 633 | Hold is `SetSquadMode` 2 with the spot in `Cell` and the facing's end in `Cell2` (null: none): no new `CommandKind`. The threat is that end, clamped to 4 to 30 cells from the spot; without a drag it is 10 cells ahead along the way the squad walked (or faced). Decision to confirm | a separate `HoldSquad` command; a facing as an angle | squads-s2 |
| 634 | A Hold more than 12 cells (the scoring radius) from the squad's anchor marches there first in March's file and picks on arrival; nearer, it stops and picks at once. A spot it can't reach: it picks round where its anchor stopped. Decision to confirm | always pick at once, members walking on their own routes | squads-s2 |
| 635 | Moving a holding squad keeps Hold: it leaves its positions (garrisons out, the mortar packs up), walks as March and picks again at the goal facing the way it went. Attack, Guard, Stop and the other orders of their own keep the picks: once done the members walk back to their Hold cells. Decision to confirm | Stop ends Hold | squads-s2 |
| 636 | Candidates: cells within the radius of the spot's centre, the nearest first (distance, row, column), the cap counting every cell on the map looked at; clear (not rock, building, rubble or water), passable to the squad's class and in the same connected region as its leader. Units standing in a cell do not make it taken (only teammates' picks do). Decision to confirm | skip cells with a unit in them | squads-s2 |
| 637 | Cover points: a blocked cell (rock, a building, rubble) among the 3 neighbours toward the threat 60, a trunk (standing or fallen) there 30, grove 25, dense 40, a crest 50; summed, capped at 100. A crest: the ground between the cell and the threat above the straight line from 1.5 m over the cell to 1.7 m over the threat (the treetops do not count). The engine has no field walls (a client paint layer): rock and buildings stand in for them. Decision to confirm | walls as a terrain flag; cover scaled by the blocker's height | squads-s2 |
| 638 | Sight: three lanes (at the threat and 6 cells either side), walked with the fog's rules from a soldier's eye over the cell out to the squad's longest reach (at most 30 cells, 64 steps); a lane ends at rock, a building or rubble (shots stop there, though fog sees past buildings). Per soldier: half the share of the lane cells seen out to his reach (his sight or ground weapon range, the longer), half the aims seen within it. Decision to confirm | lanes along the facing only; buildings not ending lanes | squads-s2 |
| 639 | A sixth to eighth factor beyond the spec's five: *Trees* (canopy that hides from the air, for the air guard's "under trees"), *Open* (open ground and roads in the lanes, for the flank's "roads and open ground") and *Near* (closeness to the spot, keeping the line and the medic central). Decision to confirm | fold them into cover and sight | squads-s2 |
| 640 | Range: 100 at three quarters of the soldier's longest ground weapon reach, linearly less either side (67 at full reach), 0 beyond reach or inside its minimum; a building adds its garrison range bonus (2 cells). Iglas (air only) and the medic have none. Decision to confirm | the weapon the role is for | squads-s2 |
| 641 | Weights (cover, trees, sight, open, spacing, safety, range, near): line 5 0 3 0 2 0 1 5, medic 5 0 0 0 1 2 0 6, recon 2 0 6 1 1 3 2 0, flank 3 0 4 4 2 0 3 1, long flank 2 0 6 2 1 2 4 0, air guard 1 8 0 0 3 1 0 1, support 3 0 0 0 1 3 3 0. Tuned on the test map: with near 3 the third rifleman went to a wood 9 cells back; with safety 4 the medic did. Decision to confirm | the owner's playtest values | squads-s2 |
| 642 | Back is measured from the line: the least depth behind the mean depth of the line soldiers placed before him, or behind the spot before any (recon and long flank pick before the line, so theirs is from the spot). Medic 2 cells, support 10, recon and long flank 0, the rest none. Decision to confirm | all from the spot | squads-s2 |
| 643 | Apart: 1.5 cells from any teammate (so never one cell for two), air guards 8 cells from each other (spec); spacing full at 3 cells for the line, 2 the medic, 4 recon and the flanks, 8 the air guards, 3 the support. With no cell left within the limits a soldier takes the best ignoring depth and apart (never another's cell); with none at all, where he stands. Decision to confirm | leave him without a pick | squads-s2 |
| 644 | Garrisons: a building is a candidate when its footprint comes within the radius, its side may enter it, it has room (stage capacity less occupants less picks on their way), and a line of sight runs from its window height (3 m) at the footprint cell nearest the threat to a body at the threat; its factors are cover 100, trees 0, sight from the cell outside that edge, safety and near from the edge. Only line, flank and recon roles (riflemen, RPGs, the sniper; the spec's list) whose reach plus the range bonus covers the threat. Decision to confirm | the Kornet team in buildings too | squads-s2 |
| 645 | A member picked for a building is sent in; his pick's cell is the door nearest the scored edge, his slot while outside; idle outside, he tries to enter again every second (phased by ID). One inside another building comes out (at its door nearest his position). Decision to confirm | a fallback cell outside | squads-s2 |
| 646 | The mortar (any member with a set-up time) sets up when it stands within half a cell of its Hold cell, idle and stopped, where it can set up; leaving Hold packs it up. Decision to confirm | the mortar also pre-aims at the threat | squads-s2 |
| 647 | Joining and losses: a soldier who joins a holding squad picks the best cell left (the others keep theirs; it costs a full scoring); a lost or detached one's pick goes. Several squads: the cells held by the owner's other holding squads count as taken. Decision to confirm | re-pick the whole squad | squads-s2 |
| 648 | Pacing: at most 2 squads pick in one squad pass, in ID order; the rest wait a tick (50 at once are placed within 25 ticks, 1.25 s). A code constant (like the path system's request budget), not content: it changes when, not where. Decision to confirm | a cell budget; content | squads-s2 |
| 649 | Hashing: per squad the Hold spot, facing, threat, the placed, waiting and faced flags and the picks (unit, cell, building), in `World.Hash` and `HashReference` after the trail (squad games only: games without squads hash as before). Candidates, factors and the debug view are derived, not hashed. Decision to confirm | none | squads-s2 |
| 650 | `SquadHoldScores(member)` recomputes the member's view on demand (allocates; for a debug overlay, not every frame) with his teammates' current picks taken: for the last picker it is exactly what he saw. It reads the world only (routing's caches may be brought up to date). Decision to confirm | a stored score grid per squad | squads-s2 |
| 651 | Golden re-recorded for the content hash only (the `hold` part of the `squad` block; the bots form no squads). `BuildingRotationTests`' old-town hashes re-pinned for the content hash (with the content before S2 the old values reproduce). Decision to confirm | none | squads-s2 |
| 652 | Not built in S2: Ambush (S3), the air ambush (S4), the skirmish AI's squads (S5); members do not turn to face the Hold facing (they face where they walked; units fight as today); a holding squad does not re-pick when the ground changes (a building falls, trees burn) until its next order; no Hold for loose soldiers; the client part (the Hold button and facing drag, the arrow, the debug overlay, `--demo=squads`). Decision to confirm | none | squads-s2 |
| 653 | Ambush is `SetSquadMode` 3 with the zone's start in `Cell` and its far end in `Cell2`: no new `CommandKind` for the order. The drag's direction says where the enemy comes from: the start is where it comes in, the far end where the column is stopped (the anti-tank mines from there back, the far-end trigger). Decision to confirm | a separate command; the zone without a direction (both ends far) | squads-s3 |
| 654 | Zone lengths are cell-centre distances (6 to 30 cells, `length` in content); outside them, or no `Cell2`, the order is refused with `OrderRefusalReason.ZoneLength` (appended): one refusal per squad, its leader as the unit, the zone's start as the cell; the squad keeps its mode and order. Decision to confirm | clamp the zone; a refusal per command | squads-s3 |
| 655 | Fire is a new `CommandKind.SpringAmbush` (49): squads in Ambush that are Setting or Ready spring on the next squad pass (Order); ignored otherwise. An Attack or Attack ground given to a member of such a squad springs it too (Order): the player shot first. Decision to confirm | reuse Stop or SetStance; attacks not springing it | squads-s3 |
| 656 | A Move or Attack-move ends the ambush and the squad takes March (garrisons out, the mortar packs up, laid mines stay on the map and in the squad's list until its next ambush): a kill zone belongs to its place, so "moving keeps the mode" (spec section 3) is not kept for Ambush. Another mode or Hold ends it the same way. Orders of their own keep it, as in Hold. Decision to confirm | keep Ambush and set it again at the goal with the zone shifted | squads-s3 |
| 657 | The side: both sides are scored with Hold's machinery (each member's best cell left, in Hold's pick order) and the side whose picks sum higher is taken, a tie the right (the clockwise quarter turn of start to far end). Members are scored on one side only: no one ever stands on the far side. Several of a player's squads given the same zone take the side the first one picked. Decision to confirm | a cheaper per-cell side score; a side per squad | squads-s3 |
| 658 | One side a squad pass: the right is scored (its picks and worth kept in the squad, hashed) and the left the next pass; an ambush's first picking costs Hold's whole per-pass budget (2), a joiner's picking 1. That keeps a pass under about 0.3 ms of picking (the bench's p99 0.48 ms instead of 0.75 ms with both sides in one tick). Decision to confirm | both sides in one pass | squads-s3 |
| 659 | Candidates: the cells beyond the zone's width (more than 2 cells off its line) on the side, within 12 cells of the zone's segment (end caps round its ends), the nearest the segment first (distance, row, column), at most 600 looked at per side; reachable and clear as in Hold. A 20-cell zone gives about 360 cells a side. Decision to confirm | a rectangle; the spot's 12-cell circle as in Hold | squads-s3 |
| 660 | Factors: Hold's eight with the zone as the threat (cover from the zone's nearest point, sight lanes to its middle, start and far end, range and near to the segment, safety the depth back from it; the three lanes are those of Hold with these aims), plus end, lane (the lane to the approach point, 10 cells before the start, seen out to his reach), approach (that point seen within his reach) and hidden (none of the three zone aims seen). The approach lane is walked only for roles that weigh it. Decision to confirm | the spec's five factors only | squads-s3 |
| 661 | Role weights and limits as in the design table: line 3 to 8 cells off the zone's line (spec); flank 2 to 9 cells, ends in turn by ID (first the far end, then the start); long flank toward the far end with the lane down the road (end 8, approach 1: with 4 and 3 the Kornet went 7 cells before the start, where the woods let him see the approach); recon 3 cells behind the line toward the start watching the approach; medic 2 cells behind the line and out of sight of the zone (a hard limit); support 8 to 12 cells off the line (the mortar's 8-cell minimum range); air guard as in Hold (the air corridor is S4). Tuned on the tests' road map. Decision to confirm | the owner's playtest values | squads-s3 |
| 662 | Garrisons: a building is on the side when its footprint cell nearest the zone lies beyond the zone's line on that side; its sight is from window height there to the zone's nearest point; it counts for line, flank and recon whose reach plus the range bonus covers the zone from that edge (the spec's list). A member with mines to lay lays them first, then goes in. Decision to confirm | buildings on either side | squads-s3 |
| 663 | Mines are dealt round the layers in ID order one at a time per kind (anti-tank, then anti-personnel, then booby traps), so each layer's first mine is the farthest left and the far end is mined first in time. Layers: the line role (`mines.layers`): the assault soldier's AP and AT kits, the engineer's trap kit (one booby trap: `trapsEach`); the RPG's booby trap is not laid (the spec names the assault soldiers and the engineer). Decision to confirm | all of one layer's mines before the next; every member with a placer kit | squads-s3 |
| 664 | Anti-tank mines: stations 2 cells apart along the zone from the far end back, each the first road cell across the zone's line within its width (the line, then a cell to the squad's side, then the far side, ...), else the line itself; as many as the layers' charges. Anti-personnel mines and traps: off the road 1 to 4 cells off the line on the far side, the best cover against fire from the squad's side first, then the far end, 3 cells apart and a cell from the anti-tank mines; then the near side if too few. No cell another of the player's ambushes plans; not water. Decision to confirm | AP mines on the squad's side too; along the road's verges only | squads-s3 |
| 665 | Laying uses the ordinary `UseAbility` order (walk within 1.5 cells, 1.5 s, the kit's cooldown); a task is done when that order ends, laid or not. A layer inside a building comes out first. When it springs (or Setting times out) the tasks not done are given up and their layers stop. Decision to confirm | a separate laying state | squads-s3 |
| 666 | Hold fire is `Stance.HoldFire` on every member, enforced each squad pass while Setting or Ready, from the order (also on the march there) until it springs. A stance given meanwhile sets the squad's only. Hide's concealment needs no stance: the canopy hides a soldier who does not fire (decision to use Hold fire, not Hide: the spec's word, and Hide switches radars off). Decision to confirm | Hide; Hold fire only once placed | squads-s3 |
| 667 | Ready: every member at his position (within a cell of his cell's centre and stopped, or in his building), his mines laid, a set-up weapon set up; or after 60 s of Setting (`setTicks`), the mines left given up. The mortar faces the zone's middle as it sets up (pre-aimed); sprung, it fires at what is in the zone first. Decision to confirm | no time limit; the mortar fires at the zone's middle on the spring | squads-s3 |
| 668 | Triggers, checked once placed and not marching, each squad pass in this order: far end (an enemy with the `vehicle` tag within 3 cells of the far end along the zone, within its width), crowd (3 enemy ground units in the zone), spotted (any member on the map in an enemy's `SpottedBy`); the zone counts only what the squad's side spots. Noted as they happen and sprung on the next pass, even on the march: a trap going off within the zone's width plus a cell (any trap: its own mines, the enemy's, a chain), a member losing HP, Fire. Triggers count during Setting too: a column arriving early is fought. Decision to confirm | triggers only once Ready | squads-s3 |
| 669 | Sprung: everyone takes the squad's stance; no member of a squad in Ambush (not marching) ever chases (`CanChase` false: retaliation, Aggressive and guard chases); the target priority's top key is the role's preference (RPGs and the Kornet vehicles, the line infantry, the mortar what is in the zone); a first target is picked on the next look (within 3 ticks). Decision to confirm | a forced search on the spring tick | squads-s3 |
| 670 | Rearm: after 10 s (`rearmTicks`) with no member's auto target, chase, attack order or HP lost and no spotted enemy in the zone, it is Ready again (Hold fire, `SquadEventKind.Ready`) and every trigger applies again; mines are not laid again (only a new Ambush order lays the stock left or refilled). Decision to confirm | Ready only on the player's order | squads-s3 |
| 671 | Hashing: per squad, once it has ambushed (games and squads without an ambush hash as before), the zone, side, spot, phase, last and pending triggers, the order's tick, the calm count, the tasks (unit, cell, kind, ability, state), the mines laid and the first side's picks and worth, in `World.Hash` and `HashReference` after Hold's state. Golden re-recorded for the content hash only (the `ambush` part; the bots form no squads); `BuildingRotationTests`' old-town hashes re-pinned for the content hash (with the content before S3 the old values reproduce). Decision to confirm | none | squads-s3 |
| 672 | Not built in S3: the air ambush (S4: Igla spacing along an air corridor, its 2-Igla trigger); the skirmish AI's squads and ambushes (S5); a debug score view for the ambush (Hold's `SquadHoldScores` covers Hold only); re-picking when the ground changes; members turning to face the zone; the client part (the Ambush button and zone drag, the dashed strip and shaded side, own-mine marks, the F key, the card's states, `--demo=ambush-road`). Decision to confirm | none | squads-s3 |
| 673 | The air ambush is the same Ambush order (`SetSquadMode` 3); the squad's make-up picks the kind at the order: air guards more than `mostly` percent (50: more than half; half is a ground ambush). The kind is kept until the next order even if members die or join. `World.AirAmbushSquad` tells the client before it orders. Decision to confirm | an order flag; a new `SquadMode` | squads-s4 |
| 674 | The corridor is 20 to 120 cells (`length`, cell-centre distance), refused outside as a ground zone is (`ZoneLength`, one per squad); `Cell` where aircraft come from, `Cell2` where they fly on to. Several squads in one command each take their own kind and limits. Decision to confirm | 30 to 200 cells; a corridor without a direction | squads-s4 |
| 675 | Igla slots: on the corridor's line, centred on its middle, the length shared out between them, clamped to 8 to 15 cells (`gap`); too many Iglas for the corridor spread past its ends at 8 rather than closer; a lone Igla stands at the middle. Decision to confirm | Iglas off the line on both sides; spreading only within the corridor | squads-s4 |
| 676 | Igla cells: the best within 4 cells of his slot (`search`) for weights [cover, trees, spacing, near] 0 8 3 2, in ID order along the corridor; hard: 8 cells from every Igla placed (the player's other holding and ambushing squads' too, near the corridor) and 1.5 from anyone; none: 8 cells round, then past the 600-cell cap 16 and 32 cells round; he never drops the 8 cells (owner: never bunch); none at all: where he stands. Decision to confirm | relax the gap last; one global assignment | squads-s4 |
| 677 | The Igla's cover weight is 0: under trees is what hides him (trees 8), the ground approach is the guards' job, and with no cover weight the Iglas' cells skip cover's walk to the approach (most of the cells a picking scores). Decision to confirm | cover 1 (as in Hold and S3) | squads-s4 |
| 678 | Guards: every member not an air guard; the ground approach 10 cells before the corridor's start (`approach`: the aircraft's side); dealt round the Iglas nearest it first, in ID order; each 2 to 5 cells (`guard`) from his Igla and nearer the approach (the medic behind, farther), weights 5 2 2 4 near the point 3.5 cells toward it; then 8 cells round, then without the guard limits. Decision to confirm | the approach dragged by the player; guards in a ring round each Igla | squads-s4 |
| 679 | An air ambush lays no mines and uses no garrisons (the Iglas stay out anyway; the corridor is not a road). Decision to confirm | the guards mine the approach | squads-s4 |
| 680 | Hidden: Hold fire from the order until it springs (S3's rule), so an Igla neither tracks nor locks while Setting or Ready; the canopy hides those under it from aircraft. Decision to confirm | Hide stance | squads-s4 |
| 681 | The Air trigger (`AmbushTrigger.Air`, appended): an enemy flying aircraft (a flight block, not on the ground) within reach of 2 Iglas (`trigger`), each by his own sight (air vision x signature; no team cueing), the slant within his weapon's range and not inside its minimum, under its ceiling, a class it engages, a missile left. Checked once placed and not marching, each squad pass, like S3's. Hurt and Order apply; far end, crowd, mine and spotted do not (a ground enemy is fought once a guard is hurt). Decision to confirm | the spotted trigger too; team cueing counts | squads-s4 |
| 682 | Volley target: kept while one of the squad's Iglas reaches it (the trigger's test); else the enemy aircraft most of them reach, ties to the lowest ID; none: 0. Picked in the squad pass, before air defence runs. Decision to confirm | each Igla's own pick | squads-s4 |
| 683 | The coordinated launch: an Igla who can engage the volley target takes it; one loaded, in reach, with no missile of his side at it (the usual one-at-a-time rule) takes part; locked ones wait until all taking part are locked or 1 s (`volleyWait`) after the first; then all launch in one tick, in ID order, after the air defence loop and before the self-protection pass (`SquadEventKind.Volley`). The wait's start is hashed (`VolleySince`). Decision to confirm | fire when 2 are locked; no wait limit | squads-s4 |
| 684 | No change to the flare logic: it already decides per missile (a salvo each within the window, at most `perMissile`) and breaks against one missile at a time (`breakPk`), so missiles in the air together are what beat it (the break and its better decoy cover one). Measured over 12 seeds: flares left singles 30.0% of their chance and a volley's missiles 65.6%; 1.4% against 10.3% a missile; the jet down 0 against 6 times in 12 (design section). Decision to confirm | a saturation rule in the dispenser (a salvo per second) | squads-s4 |
| 685 | Rearm: an air ambush is calm when no member has a ground or air target (the Igla's track now counts in a ground ambush too), chases, has an attack order or lost HP, and no enemy aircraft is within an Igla's reach; then Ready after 10 s as in S3. Decision to confirm | Ready only on the player's order | squads-s4 |
| 686 | Hashing: inside the ambush state, only once a squad has set an air ambush (S3 games hash as before): the kind, the volley target, the volley's wait start, in `World.Hash` and `HashReference`. The aircraft list is scratch (built once a tick when an air ambush needs it). The content's `air` part is digested only when present. Decision to confirm | none | squads-s4 |
| 687 | Cost: an air ambush's picking takes a squad pass's whole picking budget (one squad a tick); its cells are scored without sight lanes (canopy, spacing, near, the guards' cover), 49 a member within the 4-cell search when it finds one there, about 1,300 a squad on the bench where neighbouring squads' Igla lines meet. Bench: the squad pass 0.33 ms of tick p99, air defence +0.25 ms, paths +0.04 ms, gate 0.61 ms < 1 ms, against the same soldiers walking to the same cells on plain moves (a basis of standing squads would charge the ambush with the walk). Decision to confirm | a basis of idle squads | squads-s4 |
| 688 | Golden re-recorded for the content hash only (the `air` part of the ambush; the bots form no squads, `BotMatchMatchesGoldenCommands` passes); `BuildingRotationTests`' old-town hashes re-pinned for the same content hash (nothing there forms a squad). Decision to confirm | none | squads-s4 |
| 689 | An Igla's reach for the trigger and the volley counts the slant from his eye; a member inside a vehicle or a building does not count (Iglas never garrison in an ambush). Decision to confirm | none | squads-s4 |
| 690 | Not built in S4: the skirmish AI's squads and ambushes (S5); Iglas re-picking when trees burn or a friendly squad moves in; a corridor drawn by the client with its limits, the Volley flash, `--demo=air-ambush`; drones (one-way attack drones are projectiles: they spring nothing, the Iglas engage them as ever once sprung). Decision to confirm | none | squads-s4 |
| 691 | SQ-FIX: a member who sets up before firing (the mortar, `deployTicks`) never takes a cell the deploy code refuses (`CanDeployAt`: the class's `deployMaxGrade`, 25% for foot, and `deploySlope`), in Hold, Ambush and the air ambush, in every pass; none left: where he stands. Decision to confirm | a slope factor scored instead of a hard limit; a set-up spot found on arrival (as the rocket trucks' DeploySpot) | squads-s5 |
| 692 | SQ-FIX: the recon's Hold weights 1 0 8 1 1 1 0 0 (were 2 0 6 1 1 3 2 0): sight leads, his rifle's range to the threat no longer counts. The sight factor still walks his own 14-cell lanes, so of two cells that both see the threat a summit and a lower slope score alike. The ambush recon weights are unchanged. Decision to confirm | a height-above-surroundings factor (as the observation post's high-ground vision) | squads-s5 |
| 693 | SQ-FIX: the line's Hold nearness weight 4 (was 5): a building in reach and sight of the threat is taken over a covered cell by the spot. 3 or lower sends riflemen to cover 9 cells behind the spot on the crafted test map. Decision to confirm | 3 with a depth limit for the line | squads-s5 |
| 694 | SQ-FIX: an ambush's RPG at the tail's end (the zone's start, the second dealt) keeps to the far half of his `lateral` band (5.5 cells or more off the zone's line for the flank's 2 to 9), deeper in cover, so the column passes him before it sees him; the far-end RPG is unchanged. A rule, no new content field. Decision to confirm | a content field per end; placing him back along the road instead | squads-s5 |
| 695 | SQ-FIX (owner's call): Mine or Spotted is usually the first trigger in a column run (the far-end AT mine trips at 3.2 cells, before the 3-cell far-end distance; the line is inside a technical's 8-cell sight); documented, the trigger distances and the line's distance from the road unchanged | a far-end distance past the last mine | squads-s5 |
| 696 | S5: the skirmish AI forms squads by default (`SkirmishAi.UseSquads`, off: as before S5); squads of at most 8 (`SquadSize`), formed with 4 or more free soldiers at home (`SquadMin`), each class dealt round the squads by role then ID (mixed squads) | squads of 10 in ID order (the engine's FormSquad) | squads-s5 |
| 697 | S5: an air ambush squad from 3 Iglas at home (`AirIglas`): up to 6 Iglas and fewer line soldiers; leftover Iglas stay loose on Guard as before; its stance Guard | Iglas spread over the ground squads | squads-s5 |
| 698 | S5: the enemy's way is toward the remembered enemy structure nearest the base, else the point opposite the base across the map's centre (fog-honest, as the waves) | a route from the enemy base along the roads (pathing cost) | squads-s5 |
| 699 | S5: the territory is within 30 cells (`TerritoryCells`) of the base's centre or one of its structures; every ambush cell and the held spot lie in it (tested on every order) | a territory from the fog's explored cells | squads-s5 |
| 700 | S5: the road ambush is a straight run of road cells toward the base, at most 16 cells (`AmbushCells`), scored on pointing from the enemy to the base, length, the middle near 16 cells out (`AmbushAt`) and woods beside it; one at a time (`AmbushSquads`), none within 10 cells of another; no road, no ground ambush | curved zones following the road; several ambushes in depth | squads-s5 |
| 701 | S5: the air corridor runs from 26 cells out toward the enemy in to 4 cells from the base, shortened to stay in the territory; under 20 cells (the rules' least) the Igla squad waves instead | corridors from seen enemy airfields | squads-s5 |
| 702 | S5: one squad holds the key spot 10 cells toward the enemy (`HoldAt`, `HoldSquads` 1), facing that way; ambush and hold together take at most half the ground squads (with 3 ground squads: only the ambush) | a hold at the base's garrisonable buildings | squads-s5 |
| 703 | S5: wave squads March with the waves and turn to Assault with a spotted enemy within 20 cells (`AssaultCells`), back to March past 30; idle out in the field they search the unseen probe nearest themselves (`Objective`'s `from`; the loose fighters keep the base-ranked choice) | Assault from the start of a wave | squads-s5 |
| 704 | S5: the endgame: a wave with nothing else left to fight sends the ambush and hold squads, disbands the air ambush squad, sends the air defence and launchers, and stops giving jobs at home (matches stalled with the infantry home) | keep the base jobs to the end (stalls) | squads-s5 |
| 705 | S5: no random draws in the AI (the world's `Rand` untouched); squads and cells in ID and row order. Golden: the bots that record it are `Bot` (random orders), not `SkirmishAi`, so S5 changes no golden command; the re-record is for the content hash of SQ-FIX's weights | none | squads-s5 |
| 706 | Not built in S5: the AI producing infantry to refill squads; Hold at garrisonable buildings; ambushes in depth or re-sited when a road is found unused; the AI flying aircraft past the air ambush; the remaining standoff when both sides are down to unarmed crews (2 of 9 seeds) | none | squads-s5 |
| 707 | `farm_barn_long` added beside the farm shed (the farm kit's second garrisoned barn): footprint 15 x 4 (sidecar `footprint_cells`), door `[7, 4]` (the front row centre; the sidecar's `door_01` at glTF x 0, z 3.9 m, in the front row), height 48 tenths of a metre (the sidecar's `height_m` 4.75, the content keeping whole tenths, so rounded up), capacity 6 and 900 HP (row 487's first tuning), every other garrison field and `buildTime` 600 as the farm shed, glyph `]` shared with it (the free glyphs are gone). In the Resistance list; placed neutral like the other garrisonable buildings. Golden re-recorded for the content hash only (the bots form no barn); `BuildingRotationTests`' old-town hashes re-pinned for the same hash and the shifted definition indices (with the content before it the old values reproduce). Tests: `TownBuildingTests` (the farm and filler theories, `TheLongBarnIsTheArtsSizeGarrisonableWithItsDoorOnTheFrontCentre`), `BuildingRotationTests` (every quarter, automatically) | 4.75 m rounded down to 4.7; a distinct glyph | `farm-barn-long` |
| 708 | SQ-ORDER: on a new order the slots go by place: each member's place along the straight way to the goal and across it (half cells to the nearest, `Squad.Places`), the role layout's slots handed out front to back to the members most ahead (one depth: right to left by place); ties role order, then ID; depths and gaps stay the role layout's (members swap slots). Decision to confirm | the gaps re-laid by the new order; places taken along the path's first legs (its jogs round a sector's entrance tilt them) | `sq-order` |
| 709 | SQ-ORDER: the head (the anchor's slot) on the move is the foremost member who may lead (not the `noLead` roles medic, recon, support); `Squad.Leader` (the ID rule: the number shown, the path's class, the re-forming point) is unchanged and is the head again at rest. Decision to confirm | the leader always the head (he walks through the file on an about-turn); the leader chosen again on each order | `sq-order` |
| 710 | SQ-ORDER: on the move the recon keeps the recon slots, and the rest of the Assault line goes only to roles that stand in it (line, flank); the rear roles swap among the slots behind the line. Decision to confirm | any role that may lead in the line | `sq-order` |
| 711 | SQ-ORDER: the places hold until the order ends (arrival, a new order, an order of the members' own); then the role order again, the members walking to their role slots at rest after arriving. A new mode on the move notes the places again; a soldier who joins on the move ranks last. Decision to confirm | the marching order kept at rest until the squad is re-formed | `sq-order` |
| 712 | SQ-ORDER: on the move a member whose slot is behind him (along the way the slot moves: the trail there for March, the facing otherwise) goes sideways to the slot's line at his own level when the straight way is clear, else stands; he never routes back, and he is not waited for (the regroup wait counts only members more than 4 cells behind their slots). Decision to confirm | he walks back slowly; he stands without closing the line | `sq-order` |
| 713 | SQ-ORDER: the anchor starts as far along its path as the least margin by which a following member stands ahead of his slot, from half a cell (a reversed file turns about where it stands); none: where it was. Decision to confirm | the anchor always starts at the leader's slot | `sq-order` |
| 714 | SQ-ORDER: on a new order whose old trail lies ahead of the anchor (the file's tail ahead along the new facing) the trail starts over, the file straight back from the anchor; otherwise the file keeps bending round the old trail (row 602). Decision to confirm | the trail always kept (the file folds back along the old footsteps) | `sq-order` |
| 715 | SQ-ORDER: on a new order the facing is the straight way from the anchor to the goal (until the anchor's first step, which faces along the path as before), so places are not tilted by a route that jogs round a sector's entrance (on row 60 of the open 128-cell test arena the route runs via row 63, 2.4 cells longer than straight). Decision to confirm | the path's way 3 cells ahead (as each step) | `sq-order` |
| 716 | SQ-ORDER: hashing: `Squad.Places` (unit, along, across) in `World.Hash` and `HashReference` only while not empty (marker 0x91AC): squads at rest and games without squads hash as before. Golden unchanged (its bots form no squad). Decision to confirm | none | `sq-order` |
| 717 | SQ-ORDER: tests: `SquadTests`' `MarchIsAFileAtTheSlowestSpeed`, `MarchPutsTheRolesInTheirPlaces` and `AssaultIsALineAtEightyPercent` let the squad form up at rest before the order (given at once, the slots go by place); `SquadAirAmbushTests.AJetRunOverASetAirAmbush` checks Ready when the jet comes on (it passed by a re-arm after the jet was shot down; the squad now arrives sooner, Ready at tick 711 instead of 802, the missiles' draws differ and the jet survives the 700 ticks). Decision to confirm | none | `sq-order` |
| 718 | MT: `CommandKind.MultiTarget` (50) with `Command.Aims` (`MultiAim`: cell, target), at most 64 (a longer command is rejected by `World.Issue`); aims off the map, twice, at the player's own units, untargetable pieces or aircraft in the air are dropped. Decision to confirm | more aims | `multi-target` |
| 719 | MT: bombers: the points become salvo aims, one fire-mode group a point; a bomber in a spread mode (carpet20, block24) takes its first mode that is not spread (salvo4, row4) while on the objective and gets its own back when it leaves (the coordinator's decision for the owner's open question). Decision to confirm | a carpet line per point | `multi-target` |
| 720 | MT: a point hit only by another point's splash does not count: every point gets its own shot (the coordinator's decision). Decision to confirm | splash counts | `multi-target` |
| 721 | MT: the skirmish AI does not use it in this job (owner answer 10). Decision to confirm | the AI uses it | `multi-target` |
| 722 | MT: a point counts as hit when its shot is released or fired, not on impact: a rocket intercepted by a Patriot still counts; an aimed-at unit destroyed by the volley fired at it is hit even though the volley is not over (found in `--demo=rocket-raid --rocketraid-multi`: 7 of 8 shelters fell to their volley's first rockets). Decision to confirm | on impact, re-fired after an interception | `multi-target` |
| 723 | MT: a shot: one round of a ground or helicopter weapon, or its whole salvo with `volleyPerTarget`; a fixed wing's pass that released (a bomb group, or a strafing pass once the bombs are gone: the jets strafe the points as Attack-ground strafes its point). Decision to confirm | guns take no part | `multi-target` |
| 724 | MT: weapon field `volleyPerTarget`, unset true for a salvo (salvoSize above 1) and false otherwise; content states it on the rocket trucks, the MLRS, the jeep's pair, the light and medium rockets and the helicopters' rocket pods; in the content hash only off the default (the golden is unchanged). The twin missile truck keeps the default: both missiles on one point. Decision to confirm | one missile a point for the twin missile truck (a content flip; re-records the golden) | `multi-target` |
| 725 | MT: `volleyPerTarget: false` on a salvo weapon: each next round aims at the unit's next point (the fewest shots, the nearest) while it is in reach, outside the minimum range and within a hull-aimed launcher's arc; else the salvo stays on its point. The field is refused on bombs, jets' guns and side cannons. Decision to confirm | refuse false on a salvo | `multi-target` |
| 726 | MT: the spare shots: the units go on spreading them over the points until they run dry, as Ground attack fires until dry (owner answer 1); a weapon without a stock never ends (as Ground attack). Decision to confirm | stop once every point is hit | `multi-target` |
| 727 | MT: the assignment: the points with the fewest shots and units first, then the point the fewest free units can take, then the nearest unit and point (straight line, squared raw), ties by unit ID then point order; greedy, not an optimal matching. Decision to confirm | an optimal matching (Hungarian) | `multi-target` |
| 728 | MT: a ground point is not given to a unit for which it is inside the minimum range from where it stands (Attack-ground closes in, never backs off); a unit that cannot move takes only points in its range; ground units and helicopters take an enemy unit only while their side sees it, aircraft also unseen. Decision to confirm | back off out of the minimum range | `multi-target` |
| 729 | MT: gunships take no part (their side cannons fire at what they see round the circle, never at a point); strike drones (the Watcher) and every other unit count their shots by `Unit.LastShot`, the jets and bombers by their passes. Decision to confirm | the gunship orbits each point | `multi-target` |
| 730 | MT: leaving: any other command to a member takes it off the objective, except settings, production and sandbox ones (weapon mode, stance, fire mode, bomb weapon, detonation, smoke, produce, rally, cancel build, sandbox); an order from elsewhere (a squad's) too; a member left idle on its point (lost, refused) gets another and never that one again. Decision to confirm | none | `multi-target` |
| 731 | MT: a jet or bomber on a strike is retargeted at its break after a pass that released (no new strike: break, extend, come round); MT orders form no elements, each jet flies its own point. Decision to confirm | elements on one point after another | `multi-target` |
| 732 | MT: one point at a time a unit: a bomber does not bomb close points together in one pass (the aims queue is not used). Follow-up. Decision to confirm | queue a bomber's share of points as aims | `multi-target` |
| 733 | MT: a unit still firing a salvo of an earlier order finishes it at the old aim; its own shots count once it ends. Decision to confirm | the salvo cut | `multi-target` |
| 734 | MT: hashing: the objectives (points: cell, target, shots, assigned, first hit, gone; members: unit, point, first counting tick, refused points, the bomber's mode before) in `World.Hash` and `HashReference` only once one was made (marker 0x3A17). Golden unchanged. Decision to confirm | none | `multi-target` |
| 735 | MT: squad members: the objective's orders go to the member alone (no squad redirect); the squad system may still move him. Follow-up. Decision to confirm | the whole squad takes a member's point | `multi-target` |
| 736 | HB: the Mi-28 type's missile range is 28 cells, 70% of the attack helicopter's 40 (the spec's "30% shorter than the Apache's standoff": the standoff is 85% of the range on both, so it is 30% shorter too, 23.8 cells against 34). Decision to confirm | 30% shorter than the 34-cell standoff (23.8-cell range) | `heli-balance` |
| 737 | HB: the Mi-28 type's "slower climb" is 12 m/s (`flight.climbRate` 120, was 150: 20% slower); the spec gave no number. Decision to confirm | 10 m/s; unchanged | `heli-balance` |
| 738 | HB: "line of sight until impact" is the existing guided-missile operator rule (no new content flag: the Mi-28 type's missile drops `fireAndForget`), taught a helicopter's view: guided while its helicopter is alive, flying and has a line of sight from its height over terrain and canopy (a smoke screen across the line breaks it); the helicopter holds its pop-up until the missile ends and guides one at a time; its reload runs from the launch (a ground operator's from the end of guidance). Decision to confirm | a new `holdLine` flag; the reload after guidance | `heli-balance` |
| 739 | HB: radar-cued fire (`guided.lockAfterLaunch`): at a vehicle its own mast radar tracks (the side's radar spotting rule: in reach, vehicles only, not in smoke) it fires from its hiding place without rising at all; the missile is lofted and no crest stops it (no loft path modelled; blocked cells still stop it). Its pop-up for any other target is unchanged. Decision to confirm | a small rise to unmask the mast before firing (so the S-400 and SA-6 types can see it); a modelled loft that a high enough crest still stops | `heli-balance` |
| 740 | HB: the ripple (`ripple` `targets` 4, `interval` 5, `cone` 30): its target first, then up to 3 more different vehicles, the nearest to the helicopter first (ties to the lower ID), for which the missile is its best weapon, in range and within 30 degrees of the nose (the mount's 15 held a line of four tanks at the standoff to two: the seeker turns onto them; `cone` defaults to the weapon's), seen or on its own radar's track (hidden: only those), 0.25 s apart; the 2 s reload runs from the first; on an Attack order too (the ordered target first). Fewer such vehicles end it early; one missile a target a ripple. Decision to confirm | all four in one tick; the ripple only on radar tracks; only on Attack-move and Guard | `heli-balance` |
| 741 | HB: the Mi-28 type's missile standoff (23.8 cells) lies within its rocket pods' 25 cells, so with pods it fights a missile attack from a run (charging in to 75% of 28 cells, 21 cells, holding the line there) rather than from cover; with its pods spent it hides and pops up. This matches the spec's "charges in", left as it falls out of the numbers. Decision to confirm | its `rotary.standoff` above 90% or the pods' range below 24 cells, so it pops up from cover | `heli-balance` |
| 742 | HB: two flight fixes the shorter missile needed, for every helicopter: the cover search keeps only points whose slant from the pop-up height is within the missile's range (they lay out of reach for the 28-cell missile behind a 30 m ridge), and a standoff hover with the target hidden at its attack height hovers as high as the line needs, at most `popMax` (before, it hovered there blind and never fired) | none | `heli-balance` |
| 743 | HB: hashing: `HeliState`'s ripple (weapon, rounds left, wait, the targets fired at) only while one runs (marker 0x819), `Projectile.Lofted` only when set (0x10F7), in `World.Hash` and `HashReference`: games without them hash as before. Golden re-recorded for the content hash only (the golden battle has no helicopter: 0 over its 12,000 ticks); `BuildingRotationTests`' old-town hashes re-pinned for the same hash (nothing there flies) | none | `heli-balance` |
| 744 | MB: the plane takes the `airlifter` air class (4 air defence missile hits, 3 of the S-400 type's, every interceptor that engages the airlifter) rather than a class of its own. Decision to confirm | its own air class and hit numbers | `moab` |
| 745 | MB: the plane's numbers: cruise 11 cells/s (11.5 full power), 300 m, 40 min of fuel, 1,800 HP, 30 flares, 120 s and 5,000 at the hangar, the gunship's airframe and landing. Starting values (owner: tune after the first test) | any | `moab` |
| 746 | MB: the straight fall: the bomb leaves with no horizontal speed (delivery `straight`: `retard` 1000 permille) and falls vertically onto the point under the release, at the real gravity (9.8 m/s2: 7.8 s from 300 m), so the plane releases over its aim. Decision to confirm | a forward throw (real physics) with the release solution leading; the bombs' compressed gravity | `moab` |
| 747 | MB: a straight bomb skips the attack run's nose-angle abort (the plane is over its aim when it releases); the past-the-window abort still sends it round. Code rule | none | `moab` |
| 748 | MB: the warning: every enemy player from the release to the impact (`IncomingStrikes`, radius the light ring's 75 cells; `IncomingEvents` Warned, Struck); nothing before the release (the plane is seen by every sensor). Decision to confirm | warn only players whose sensors see the plane; show the three rings' radii in the strike | `moab` |
| 749 | MB: the rings: core 15 cells destroys everything on the ground whatever its armour, with whoever is inside (the bunker buster's rule); heavy to 40 cells 40% (1,200) flat; light to 75 cells 10% (300) fading linearly to 0; a crest halves it outside the core; friends hurt too. Decision to confirm | a heavy ring fading too; the core by damage, not destruction | `moab` |
| 750 | MB: the `moab` warhead: infantry, light and medium vehicles and armour 100%, buildings 200%, parked aircraft 120% (bombs hit directional armour on top, 150%: an Abrams in the heavy ring keeps 700 of 2,500; the light tank and the infantry carrier die there). Decision to confirm | armour 70% for a tank that lives with more | `moab` |
| 751 | MB: garrisons: a building the blast brings down takes its garrison with it (no collapse roll); a building left standing shelters its garrison fully (the warhead is in no garrison's occupant warheads). Decision to confirm | the collapse roll; occupant damage in the light ring | `moab` |
| 752 | MB: tunnels: every entrance within 40 cells by its footprint, any owner's (the bomber's own too), collapses into rubble, everyone underground there dies and so does a unit on a passage at a place within 40 cells (the bunker buster's rule at 40 cells); the farther entrances and their units live. Decision to confirm | the whole network | `moab` |
| 753 | MB: forest: flattened within 40 cells to scrub (no cover from the air), trunks felled as stumps without fallen logs (thousands of logs would wall vehicles off and cost); the light ring's trees stand; infantry routing reads it as before (`thinnedForestAsBefore`). Decision to confirm | flatten to open ground; logs; trees bent in the light ring | `moab` |
| 754 | MB: fire: every forest cell in the core and 10 to 20 more out to 40 cells, all spreading, after the flattening; the fire rules' cap of 150 holds (over dense forest the core alone fills it). No building fire in the engine: the client draws the brought-down buildings' ruins burning. Decision to confirm | a higher cap for this blast; a building fire state | `moab` |
| 755 | MB: no limit and not in the heavy bombers' exclusive group; in the Sentinel faction as an ordinary hangar unit until the tier 2 pick lobby exists. Owner (no limit) and decision to confirm | the pick | `moab` |
| 756 | MB: ground points only (`pointsOnly`), no Guard, no automatic targets; spent, it flies home to rearm (decision 143's rule; 15 s for the bomb in a heavy bay). Decision to confirm | none | `moab` |
| 757 | MB: the ring blast is a bomb's only (a delivery block); its weapon's splash is the last ring's radius, used by the warning and the renderers. Code rule | rings on any weapon | `moab` |
| 758 | MB: hashing: the flattenings (`World.Flattens`: bomb, centre, radius, density, tick, cells, trunks; marker 0xF1A7) in `World.Hash` and `HashReference` only once one happened; the terrain digest reads flattened forest as it was. Golden: content hash only. Decision to confirm | none | `moab` |
| 759 | JR: the S-400 type does not intercept the Jericho type's missile (it stays aircraft only, as the owner specified); interception class `ballistic`, so the Patriot and THAAD types would engage it if the Resistance ever had it (the coordinator's decision for the spec's open question 1). Decision to confirm | the S-400 type gets the class at about 30% a missile while it climbs | `jericho` |
| 760 | JR: reload packed up at a supply depot or a field ammo point (the existing `depotReload`: any depot-type supply), 180 s and 1,000 supply; not at a garage (new `depotReload.noService`) (open question 2). Decision to confirm | the main base only; the garage too | `jericho` |
| 761 | JR: no alive limit (no exclusive group) for now (open question 3). Decision to confirm | one alive at a time, like the hypersonic | `jericho` |
| 762 | JR: no cluster warhead upgrade now (open question 4). Decision to confirm | a cluster upgrade against vehicle parks and infantry | `jericho` |
| 763 | JR: no pick hook: built as an ordinary Sentinel unit (its line in `factions/sentinel.json`) at the vehicle garage and the military garage (both build Sentinel `vehicle`s) until the pick lobby exists. Decision to confirm | only the military garage | `jericho` |
| 764 | JR: the vertical start is a new generic `terminal.boostTicks` (120): straight up, pitching over onto the arc with a continuous velocity, its height derived per shot (`B = k vc T / 2`); shortened on a short shot so the boost covers at most half the way to the peak. Decision to confirm | a fixed boost height in content (a kink where the arc takes over) | `jericho` |
| 765 | JR: the profile: 119 cells/s over the ground (25.05 s at 2,500 cells, the spec's "about 25 s"), peak half the range from 600 m to 2.5 km at 60% of the way, separation 20 cells out and 200 m up, the last stretch at the same speed (no jump), no weave; the fall is about 66 degrees at the separation and 81 at the end. Decision to confirm | a slower, longer visible fall (a lower `speed`) | `jericho` |
| 766 | JR: damage on a new `heavy_ballistic` warhead (the ballistic table, buildings 250%) so the spec's "kills any building" holds (6,250 against the heavy hangar's 6,000); "the heaviest single hit" holds against every missile, shell and rocket, not against the bombs (the MOAB type bomb, landed first, 3,000 with a 15-cell core that destroys everything; the bunker buster, 6,000 within 3 cells). Scatter 2 cells and the 8-cell forest fire were not in the spec. Decision to confirm | the plain ballistic table (5,500: the hangar survives); damage above 6,000 | `jericho` |
| 767 | JR: the lingering launch smoke cloud is client only (no smoke screen in the sim, no sight or lock blocking). Decision to confirm | a sim smoke screen at the truck for 20 s | `jericho` |
| 768 | JR: the truck is named "Ballistic missile truck", glyph `^`, tags `vehicle`, `launcher`, `heavy`, a driver, vision 5 cells, accel 3 s (the spec was silent). Decision to confirm | none | `jericho` |
| 769 | JR: hashing: the content hash adds `boost` (BoostTicks) and `noService` only when set; nothing new in `World.Hash`. Golden re-recorded for the content hash only (its battle has no Jericho type; replay commands unchanged, `BotMatchMatchesGoldenCommands` passes); `BuildingRotationTests`' old-town hashes re-pinned for the same hash. Tests that named the extremes updated: the hypersonic missile is the Resistance's longest shot, the tactical missile truck the slowest vehicle but the Jericho type, and the Jericho type's missile kills the infantry carrier (`CarrierTests`). Decision to confirm | none | `jericho` |
| 770 | Player AI v1: opt-in host-owned PlayerAi emits only normal delayed commands; one instance per player and World, no direct unit changes or RNG draws. Existing Bot/SkirmishAi unchanged; replay runs commands without the bot. Live bot state restore is not supported yet | replace SkirmishAi or execute the planner inside World | codex/player-ai |
| 771 | Player AI policy version 1 lives in content/ai/player.json, outside hashed simulation content; strict separate loader validates bounds. Durations ticks, distances whole cells. Replays retain commands, not policy-dependent regeneration | add policy to the content hash and invalidate old replays | codex/player-ai |
| 772 | Player AI reads owned units and currently visible enemies. Only structures are remembered; an old cell must be observed empty before forgetting. Reachability and placement use normal physical world services, not a separate fog-filtered navigation map | global enemy knowledge; mobile-contact prediction in v1 | codex/player-ai |
| 773 | Player AI reserves exclusive mission members and exploration probes; Scout, Assault, Defend, Recover use bounded assembly, progress, mission and recovery deadlines. Existing squads recruited whole, when they fit. Default force 8, minimum 4, assembly 400 ticks; no role-specific launch minimum yet | timed waves without persistent objectives | codex/player-ai |
| 774 | Player AI recruits at 65% HP; retreats below 35% aggregate HP or below 50% launch members, all ground weapons empty, expiration or no progress. Rest 200 ticks after release. Current visibility of a fight refreshes progress. Values in policy | per-unit combat-power prediction and adaptive retreat | codex/player-ai |
| 775 | Player AI reserves 2 home guards while other fighters exist and releases the last defenders. One scout, 16-cell probes, revisit/failed-probe delay 1200 ticks. Compatible infantry forces form squads and use March/Assault hysteresis at 20/30 cells | keep every defender at home to the end | codex/player-ai |
| 776 | Player AI produces one configured ground unit per decision, counts alive/queued/bay/door/in-flight definitions, enforces factory tags and queue limits and a 128-unit cap including produced drivers. Construction/production Charge remain engine no-ops; no invented cash economy or infantry spawning | fake resource budgeting or SandboxSpawn reinforcement | codex/player-ai |
| 777 | Player AI resumes planned unfinished sites before new construction. Placement facing 0 uses World.CanPlace, a visible centre, no visible enemy within defense radius, bounded cursor and retry delay. One unfinished project at a time | rotation-aware base planning or multiple simultaneous projects | codex/player-ai |
| 778 | Player AI crews empty friendly vehicles near home with legal unreserved soldiers. Cargo reserves low-stock stores or sources, uses LoadCargo/Unload, limits trip length and duration, stops expired trips and respects normal finite stock | direct boarding/refill or duplicate delivery assignments | codex/player-ai |
| 779 | Player AI command/mission/placement work bounded in policy; fixed ID/index order and rotating mission updates, no elapsed-time decisions. Bounded lifecycle trace and detached mission views are diagnostics only | timing-dependent AI budgets | codex/player-ai |
| 780 | Player AI v1 scope: ground missions, construction, ground production, cargo, support fire and infantry squads. Autonomous aircraft strategy, role-specific composition, counter-production, live bot saves and win-rate/performance tuning deferred. Source research: OpenRA, Petra, NullBot and Generals; independent implementation | port reference game code or claim competitive strength without playtests | codex/player-ai |
| 781 | Player AI foundation extends decisions 770–780: optional foundation policy enables strategy, operations, specialists and checkpoints; null retains the first ground-policy path. Old bots and world systems stay separate | replace historical bots or claim tuned strength | codex/player-ai |
| 782 | Enemy contacts are frozen visible observations; mobile strength decays and contacts expire, structures retain observed-empty forgetting. Capability roles derive from compiled definitions and tag overrides | hidden live enemy resolution | codex/player-ai |
| 783 | Integer strength/danger heuristics, posture and ranked objectives use observed value, distance, defense, assignments and expiring failed-objective penalties. Stable IDs/cells break ties; no learned weights | battle simulation or global enemy strategy | codex/player-ai |
| 784 | Recruitment fills role quotas first. Essential quotas block deadline launch; optional quotas relax at deadline. Existing squads remain whole and membership exclusive | steal squad members or wait forever for an essential role | codex/player-ai |
| 785 | Coordinated operations stage forces on alternating reachable approaches, release on arrival quorum or staging deadline and recover on expiry/lost intelligence. Approach candidates and active operations bounded by policy | guarantee synchronized terrain travel | codex/player-ai |
| 786 | Reserved specialist executors issue normal aircraft search/cover/strike/rearm, garage service, repair-follow, artillery, radar/air-defense and cargo escort commands. Deadlines cancel or return units; no direct engine state changes | free service or specialist orders competing with force orders | codex/player-ai |
| 787 | Ground/rotary transport jobs reserve loose passengers, bound boarding, insert and unload, cancel unboarded orders and retain reservations during abort unloading. No fixed-wing vehicle airlift executor | direct teleportation or arbitrary boarding | codex/player-ai |
| 788 | Production uses capped observed-threat goals, delays, normalized deficits, existing producer restrictions and aircraft stations. Construction cycles four facings and can anchor forward goals; Charge remains a no-op | invent cash or infantry producers | codex/player-ai |
| 789 | Charged launch abilities use normal UseAbility for observation aircraft or visible-target strike bodies. Mine placement, garrisons, ambush designs and loadout switching remain follow-ups | implement every special ability generically | codex/player-ai |
| 790 | Versioned planner capture/restore includes clocks, probes, intelligence, missions, operations, jobs and reservations; validate world/player/content/match/policy and ownership before mutation. Host restores world and pending inputs separately; SimRun pairs state with replay | treat planner JSON or World.Hash as a full world/input-queue save | codex/player-ai |
| 791 | Owner requested expanded foundation code without local testing. Compile only; prior ground-only tests/CI are not evidence for expanded head. Behavioral, continuation, platform and performance validation remain required | claim a finished competitive AI from code volume | codex/player-ai |

| 792 | Player AI posture control: optional complete five-profile table, empty retains prior foundation allocation. Shipped Rebuild/Defend/Recon disallow assault commitments, Expand allows 1 force and Attack 4; all assembly/staging/active assaults count | posture only supplies diagnostic context (decision 721 extension) | codex/ai-strategic-control |
| 793 | Posture minimum dwell 200 ticks; visible ground intruder near a friendly structure bypasses dwell and strategy interval on next decision. Existing strength threshold can still choose defense. Hold clock is planner state | wait for the ordinary assessment before defending | codex/ai-strategic-control |
| 794 | Defenders reserve ceil(percent of mobile fighters), at least homeGuards, at most fighter count: Rebuild 60%, Defend 80%, Recon 25%, Expand 30%, Attack 15%. Whole forces/squads can over-reserve; unreachable/unready forces can leave a gap. Defense commands and allocations precede specialists | split squads to hit exact shares or always release the last defenders | codex/ai-strategic-control |
| 795 | Excess assaults prefer retaining active, then staged, then assembling forces with ID ties. Recalls become reachable home defenders or Recover; a guard gap may also recall a permitted assault. Scout limits follow posture. Deliberate recalls do not penalize objectives as defeats | treat strategic withdrawals as failed enemy objectives | codex/ai-strategic-control |
| 796 | Nonoffensive posture cancels distant air/artillery strikes, moves air/radar cover home, denies new insertions and unloads cancelled transport passengers before release. Visible targets near friendly structures remain legal defensive strikes. Recon/service remain available | continue distant specialist attacks while ground offensives are forbidden | codex/ai-strategic-control |
| 797 | Per-posture integer infrastructure/logistics/recon/defense/combat weights plus observed-counter bonus prioritize normalized production/construction deficits; ties retain policy order. Current unfinished sites resume first. Forward permission off places forward goals at home | fake credit spending, cancel engine queues or abandon sites on posture change | codex/ai-strategic-control |
| 798 | Planner checkpoint format 2 includes posture hold clock and profile policy digest; reject format 1. Follow-up compile only at owner request, no local tests/matches; tactical and continuation validation remain open | silently drop strategy state or claim behavioral validation from compilation | codex/ai-strategic-control |
| 799 | Optional mobile ground force tactics: nearest 12 visible candidates, ID ties, within 24 cells of a member. Focus requires legal in-range weapons on 50% of the force. Integer score uses armor-adjusted weapon effectiveness, return-fire pressure, objective, persistence, missing HP and distance; equal score lower ID | hidden targets or generic nearest-enemy fire without weapon suitability | codex/ai-force-tactics |
| 800 | Focus orders preserve whole force/squad membership, leave persistent objective unchanged, and defenders select only enemies near friendly structures. Existing command cap/input delay apply, with 20-tick tactical retries; no projectile-arrival or overkill prediction | split squads for per-shooter orders or mutate unit targets directly | codex/ai-force-tactics |
| 801 | Active assaults spread over 12 cells from center can rally at a commonly reachable actual member position, 8 candidates, 160-tick deadline, 400-tick retry. Visible local contact interrupts; mission depletion/stall/lifetime rules remain. No indefinite rally holding | continually extend regroup or erase strategic objective | codex/ai-force-tactics |
| 802 | Planner format 3 saves rally point/deadline/retry, policy digest covers tactical fields; reject earlier formats. Astra audit remains parent evidence; tactical follow-up compile-only at owner request, no local tests/matches | claim parent's green tests validate new tactical code | codex/ai-force-tactics |

The owner answered D6 (flares and chaff, built in E6) and D15 (landing on `main` only when everything passes). D12 (bomb
smoke), D14 (smoke above the fog) and D16 (the flipbook bake) are client and VFX calls; D13 as recommended: the engine
digs craters only in runways (no terrain craters or trees knocked down in the sim yet).

### Not yet

The skirmish AI does not fly aircraft. CS / CT airstrike visuals and C7's crash and ejection visuals are client work;
the bomb-smoke flipbook (D1 bake) waits for the owner's go. Resistance aircraft (and so enemy jets for the Sentinel's
radar missiles to meet in a real match) come later. No map wind; no per-weapon rearm times; drones have no wreck.

## Terrain (data foundation)

Elevation, roads and forest are a map's terrain (`Sim/TerrainMap.cs`); a map without one is flat
and plays and hashes exactly as before (`World.Ground` is null and nothing is added to the
hash). Everything is integer and compact, with no per-cell objects:

- **Heights** per cell corner, (W+1) x (H+1) values in the sim's fixed point (1024 per cell = 2 m),
  4 bytes each (up to ±256 m since 882c057; they were 2 bytes, ±64 m, before). `HeightAt(pos)` is bilinear between the
  cell's four corners, rounded to the nearest raw unit (halves up); off the map it takes the nearest edge.
- **Grade** of a cell: the largest corner height difference over its width, in percent.
- **Movement classes** (`content/terrain/rules.json`, `TerrainRules`): foot, wheeled, heavy
  wheeled (radius 1 cell and up: the trucks) and tracked. Each sets where Gentle, Steep and Cliff
  begin (10 / 25 / 45 % to start), speed percent up and down per slope class, path cost per
  slope class (flat open ground 10, 0 = impassable: wheeled vehicles can't climb Steep, nobody
  climbs a Cliff), the extra cost of a climbing step, road speed and cost per road kind (see
  "Roads, props and terrain"), and forest cost, speed and blocking per density (heavy wheeled and
  tracked can't enter dense forest).
- **Layers:** a flags byte per cell (forest density 0-3: none, scrub, grove, dense; road;
  building pad; the road's kind in two bits, 0 being paved so a plain road hashes as before; water; a deck over water), a second flags byte only on a map with alleys or footpaths ("Alleys and footpaths"), the grade byte, the cell's centre height (the sum of its corners, 4 bytes: routing and fog read it), and one cost byte per cell per movement class, built when the map
  loads and rebuilt per cell when a pad is flattened or the forest changes. 16 bytes a cell with the 5 classes and
  water: 1,024 x 1,024 cells take 16.8 MB and load in about 0.2 s, 6,000 x 3,000 take 288 MB and load in about 0.9 s
  ("Map size" below; `SimRun -terrain-bench`). The cost
  grids are what the pathfinding milestone (HPA* over 32 x 32 sectors, a road highway layer,
  flow fields) reads; `StepCost` adds the climbing penalty for a directed step.
- **Map files:** a JSON file with the size, a 16-bit greyscale PNG heightmap of the corners
  (pixel 0 = `minHeight`, 65535 = `maxHeight`, metres, default -16 to 48), an optional 8-bit
  forest density PNG (pixel / 64 = density), road polylines and rectangles (each with an optional
  `kind`: `highway`, `paved` (default), `gravel`, `dirt`, and the foot kinds `alley` and `footpath`), forest rectangles,
  flatten pads (height in tenths of a metre) and water (rivers, lakes, the sea: "Water"). The game repo's `tools/terrain/make_heightmap.py`
  generates them.
- **Since then:** movement speed (slope, forest and road kind), HPA* routing on the cost grids,
  projectiles, sight and deploy rules all read the terrain (README, "Movement" and "Terrain").

### Map size (2026-10-25)

The first real map is to be about twice the area of 6 x 6 km and not square (12 x 6 km: 6,000 x 3,000 cells of 2 m),
first at about 4 x 3 km (2,000 x 1,500). The cap was 4,096 cells a side, in the loader only.

- **The cap: 8,192 cells a side** (16.4 km; `TerrainMap.MaxSide`), width and height alike, any aspect. The loader,
  `TerrainMap` and `Grid` refuse 0 or 8,193 with "width and height want 1 to 8192 cells, got WxH". The heightmap's and
  the forest map's PNG headers are checked before their pixels are decoded (a wrong size fails before it allocates).
- **Why 8,192 is safe** (the audit): positions, distances and their squares are 64-bit raw (1,024 a cell; the fixed
  point is a `long` with 10 fractional bits), so a squared distance across 8,192 cells plus the widest fly zone (74
  cells) is about 2^47, and the mission code's segment projection (such a dot product times 1,024) under 2^58. Every
  `(int)` cast of a raw value is a cell, a height or a local offset. Cell indices (y x width + x) stay under 2^26 and
  corner indices under 8,193²; the abstract search's node ids (sector x 128) under 2^23; the searches' packed keys
  (cost << 32 | cell) hold any cost a route can have; the movement, spatial, box and flight clearance buckets are
  sized from the map. The map-size scalings of the jets (`speedByMap`, `enduranceByMap`, `heightByMap`) stop or cap at
  their content's sizes; the fly zone is the map grown by its margin, all in raw. Replays and commands store cells as
  JSON integers. Nothing ties a map to 4,096 any more; 16,384 would fit every index too, at four times the memory.
- **Height range: ±256 m** (`TerrainMap.MaxCornerRaw` = 256 x 512 raw; the loader takes `minHeight` and `maxHeight`
  from -256 to 256 m and water surfaces from -2,560 to 2,560 tenths). Not changed: the "-64 to 63 m" of the game's map
  format is the 2-byte corners' range from before 882c057. Raising it is one constant plus the two loader messages: the
  corners are ints, the bilinear products in `GroundHeightAt` stay in range up to about ±2^42 raw, the centre sums
  (4 corners) to ±2^29, the grade byte caps at 255% (steeper is a cliff anyway), fog and shells compare heights in
  `long`, and the digest already hashes 4 bytes a corner (existing maps would keep their digests). The 16-bit
  heightmap spreads 65,536 levels over the range (8 mm a level at ±256 m).
- **Memory, bytes a cell** (5 movement classes, 8 routing classes, fog for 2 players; as built at 6,000 x 3,000):

  | Layer | Bytes a cell | 6,000 x 3,000 |
  | --- | --- | --- |
  | Terrain: corners 4, flags 1, grade 1, centre height 4, cost 1 a class (5), water 1 | 16 | 288 MB |
  | The grid | 1 | 18 MB |
  | Routing grids: free and occupied 2, cost 1 a routing class (8) | 10 | 180 MB |
  | Regions: 1 a class (the crowd's shares its own class's) | 7 | 126 MB |
  | Formation marks 1, the abstract search's buffers 2 | 3 | 54 MB |
  | Fog: visible counts 2 and explored 1, a player | 6 | 108 MB |
  | HPA* sectors, as routes and quiet ticks build them (every one of the 8 classes: 18) | 0 to 18 | 0 to 323 MB |
  | Movement's wall distances, a byte for each class that has moved (6 at most) | up to 6 | up to 108 MB |
  | Flight's clearance grid (4 ints a 2 x 2 block, once an aircraft flies) | 4 | 72 MB |

  Terrain, routing and fog: 756 MB plus the sectors built. Measured (`bench/terrain_6000x3000.json`): 890 MB
  retained with every class's regions, fog and a 200-unit battle running; with every sector of every class built
  (`bench/terrain_6000x3000_prepared.json`) 1.24 GB. Before this change, at 1,024 x 1,024 with fog and every
  sector built and no units (`-terrain-bench 1024 -battle 0 -fog true -prepare true`), a world held 110 bytes a cell; now
  67. The HPA* sectors kept their n x n cost matrix besides the pruned edges (now only the edges, an int
  each: cost << 7 | node, a sector's costs are under 2^22), the regions were 2 bytes a cell and class (now 1; a sector
  with 255 regions or more, a checkerboard of single cells, keeps its own ushort labels), routing and fog each kept a
  copy of the centre heights and fog a canopy table (now the terrain's `CentreSums`, read by both; fog's rays read the
  terrain's forest and water directly), the formation marks were 4 bytes, and every spatial and box bucket had its own
  empty list (now one shared until something is filed there).
- **Speed** (M3 Pro, Release, `SimRun -terrain-bench SIZE -battle 200 -fog true`; the synthetic map: hills from -20 to
  120 m, value noise, a forest map, three roads, a river into a lake, a pad):

  | | 1,024 x 1,024 | 6,000 x 3,000 |
  | --- | --- | --- |
  | Terrain load (PNG decode, every layer) and its memory | 212 ms, 16.8 MB | 900 ms, 288 MB |
  | The terrain digest (the first hash after a terrain change) | 7.5 ms | 109 ms |
  | The world built, the terrain attached | 6 ms | 20 ms |
  | The first tick (routing's grids for the 8 classes) | 97 ms | 1.24 s |
  | Every class's regions (`PrepareRouting(everySector: false)`) | 0.34 s | 2.0 s |
  | Every sector of every class (`PrepareRouting()`) | 6.0 s | 102 s |
  | A tick of the bare map (no units), p50 / p99 | 0.002 / 0.003 ms | 0.002 / 0.006 ms |
  | The tick after a terrain change (a pad) | 0.9 ms | 0.7 ms |
  | 200 units fighting at the far corner, tick p50 / p99 (600 ticks; sectors built as needed) | 1.09 / 3.84 ms | 0.67 / 4.06 ms |
  | The same with every sector built | 0.87 / 3.40 ms | 1.03 / 3.65 ms |
  | The first tick the armies move (wall distances of their classes, built once) | 20 ms | 377 ms |
  | A technical's first corner-to-corner drive (its sectors not built) | 257 ms | 2.44 s |
  | The next one over the same ground (every sector built) | 4.6 ms (4.3 ms) | 57 ms (24 ms) |
  | 20 soldiers sent corner to corner as a group (every sector built) | 204 ms (2.7 ms) | 1.06 s (12 ms) |
  | Memory held: terrain, routing with every class's regions, fog, the battle (every sector built) | 58 MB (77 MB) | 890 MB (1.24 GB) |

  Per-tick cost no longer grows with the map: the movement bucket grid (2 x 2 cells) cleared and prefix-summed the whole
  map every tick (7 ms at 6,000 x 3,000); it fills and empties only the buckets that hold bodies now. A terrain change
  (a pad, a forest cleared) rebuilt fog's whole-map tables (150 ms there); fog re-sweeps only the viewers whose box
  reaches a changed cell now (the terrain's change journal). The terrain digest (FNV-1a over every corner and flag, the
  same bytes and value) runs in tight loops: 359 to about 110 ms at 6,000 x 3,000, so a hash right after a terrain change costs
  that once (the client hashes every 200 ticks while the ground changes).
- **Routing on a big map.** A far route's abstract search gave up after 40,000 portals and kept the nearest point: on
  6,000 x 3,000 a corner-to-corner order stopped halfway. Now, where it gave up on a goal it can reach, it searches
  again greedier (twice the weight each time, up to 8 times), and the careful search inside the route's corridor may
  expand 16 nodes for each sector of the map (at least the 40,000 it had: up to 1,600 x 1,600 cells, 2,500 sectors,
  nothing changes). Building every sector of every class (`World.PrepareRouting()`) takes 102 s at 6,000 x 3,000,
  too long for a loading screen; `World.PrepareRouting(everySector: false)` builds the routing grids and every class's
  regions (2.0 s at 6,000 x 3,000, after the 1.2 s of grids) and leaves the sectors to the routes that reach them and to quiet ticks. Then the first
  order of a class across a map whose sectors are not built yet pays for them in its tick: 2.4 s for the first
  corner-to-corner drive at 6,000 x 3,000, 57 ms for the next one over the same ground. A group of 20 soldiers sent
  corner to corner: 1.1 s, 12 ms with every sector built. Movement's wall distances are built for a class the first time
  one of its units moves (377 ms for the battle's classes at 6,000 x 3,000, 20 ms at 1,024 x 1,024).
- **For the client:** below about 2,000 cells a side keep `PrepareRouting()` in the loading screen (6 s at
  1,024 x 1,024; about 17 s at 2,000 x 1,500, estimated at the 6,000 x 3,000 rate per cell, not measured); above it call
  `PrepareRouting(everySector: false)`. A loading
  step that builds the sectors around the starting positions, or a second level of routing for maps this size (a
  graph of sectors, or a landmark heuristic), is the follow-up that removes the cold-route spikes.
- **Not yet:** that follow-up; a client measurement of a 6,000 x 3,000 map (drawing, the minimap, memory of the views);
  wall distances for every class at load (now each class's on its first move).

### Town performance (Zuberec, 2026-10-27)

The game's bench on its first real map, Zuberec (2,000 x 1,500 cells, a town file of 995 neutral buildings, the demo's
convoy), had frames that ran a sim tick about 4 ms slower than the others (tactical camera: 19.6 against 15.4 ms).
`SimRun -zuberec-bench DIR -town FILE` reproduces it headless (the client's world: pads, buildings, convoy; plus 200
units fighting round the square), with and without the town. Tick p50 after the first orders, ms (M3 Pro; phases p50;
`bench/zuberec_before.json`, `zuberec_after.json`, `zuberec_debug_*.json`):

| | Convoy, town | Convoy, no town | Convoy, town, after | Battle, town | Battle, no town | Battle, town, after |
| --- | --- | --- | --- | --- | --- | --- |
| Tick (Release) | 0.83 | 0.09 | 0.27 | 0.91 | 0.11 | 0.29 |
| Garrisons | 0.58 | 0 | 0.011 | 0.62 | 0 | 0.011 |
| Vision | 0.060 | 0 | 0.060 | 0.067 | 0.002 | 0.064 |
| Combat | 0.051 | 0 | 0.052 | 0.056 | 0.002 | 0.055 |
| Paths | 0.090 | 0.083 | 0.096 | 0.094 | 0.086 | 0.091 |
| Tick (Debug build) | 5.75 | 0.35 | 1.21 | 6.30 | 0.48 | 1.46 |
| Garrisons (Debug) | 4.56 | 0 | 0.066 | 4.93 | 0 | 0.068 |

- **The cause:** `RunGarrisons` counted each garrisonable building's occupants by scanning every unit: buildings x
  units a tick, about a million visits on Zuberec. It now gathers the live occupants' building IDs in one pass, sorts
  them and walks them beside the buildings in ID order. Ejecting a building's excess changes only its own occupants
  (they stay alive), so every count is the one the old scan found: the bench hashes, `-bench 500,10000`'s final
  hashes (`bench/garrison_bench_*.json`) and the golden replay are unchanged. `GarrisonIndexTests` run worlds with
  soldiers of both sides going in, stages dropping, occupants killed and a building destroyed against the old scan
  (`World.FullGarrisonScan`) and compare the hash every tick. Without buildings the extra pass costs about 0.01 ms at
  10,000 units.
- **The Debug build:** the client's bench builds `Client.csproj` without `-c Release`, so its sim runs unoptimised,
  about 4 to 7 times slower on these scenes; the ~4 ms it saw fits Debug's 5.75 ms better than Release's 0.83 ms.
  Either way the town's extra cost was mostly this loop.
- **Left:** the town's other ~0.2 ms a tick (Debug ~0.85 ms): the spatial index is rebuilt twice a tick (vision, combat),
  filing every building under each bucket its footprint touches, and ~25 systems pass over 1,000 more units. The
  tick the town is spawned (995 `SandboxSpawn`s, routing and wall distances catching up) is a one-off of about 250 ms,
  and the convoy's first long route about 150 to 300 ms with or without the town (sectors not built yet; "Map size").

## Buildings, garrisons and destruction

Neutral city blocks stand on the map and can be garrisoned. Each is one model with five visual stages (intact plus four destruction levels, the last is rubble) and window firing points, built from a modular kit.

| Block | Footprint | HP | Capacity |
| --- | --- | --- | --- |
| Panel block (5 floors, concrete panel) | 23 × 6 cells | 2400 | 10 |
| Brick block (4 floors, hip roof) | 14 × 7 cells | 1600 | 6 |

Smaller civilian buildings are placed the same way and follow the same rules below, one door each on the front (south)
side as laid out; any of them may stand turned, the front to the west, north or east ("Turned buildings" below). The town buildings: a small house (5 × 5 cells, 700 HP, holds 4), a walled villa (8 × 9, 1000, 6), a shop row
(7 × 6, 1100, 6) and an unfinished concrete frame (8 × 6, 1300, 8). The countryside homes (first batch, 2026-10-24, decisions
317 to 322; second batch, 2026-10-25, decisions 397 to 400; the stone village houses, 2026-10-25, decisions 424 to 427;
a first tuning):

| Home | Footprint | Height | HP | Capacity |
| --- | --- | --- | --- | --- |
| Stone farmhouse (`farmhouse_stone`: thick limestone walls, flat roof with a parapet) | 7 × 5 cells | 7.2 m | 900 | 5 |
| Unfinished house (`house_unfinished`: one finished floor, an open concrete frame upstairs) | 6 × 5 cells | 9.5 m | 800 | 5 |
| Courtyard house (`house_courtyard`: the footprint includes the walled yard; the door is the yard gate) | 9 × 9 cells | 5 m | 800 | 5 |
| Tiled villa (`villa_tiled`: two floors, red tile roof) | 7 × 6 cells | 9.5 m | 900 | 6 |
| Tin-roof shack (`shack_tin`: block walls, corrugated roof; flimsy) | 4 × 4 cells | 3.2 m | 350 | 2 |
| Shepherd's hut (`shepherd_hut`: a small hut) | 4 × 4 cells | 3.5 m | 400 | 2 |
| Mountain house (`mountain_house`: a tall house) | 6 × 6 cells | 9.2 m | 900 | 5 |
| Mud-brick house (`mudbrick_house`: a low mud-brick house) | 6 × 5 cells | 4.3 m | 600 | 4 |
| Stone rowhouse (`stone_rowhouse`: built to stand wall to wall in a row; the footprint is the house) | 4 × 6 cells | 8 m | 900 | 4 |
| Arched stone house (`stone_house_arch`) | 6 × 6 cells | 7.5 m | 1000 | 5 |
| Stone tower house (`stone_tower_house`) | 5 × 5 cells | 11 m | 1300 | 5 |

Town buildings, third batch (2026-10-25; a first tuning, decisions 401 to 407). Five are garrisoned like the homes, with
the same garrison numbers and one door on the front row centre (only the capacity differs); the kiosk is a plain neutral
prop like the chicken coop. All six are neutral, cost 0, build time 600 ticks, building armour. The L-shaped block's whole
12 × 10 footprint blocks movement, its inner yard included, like the courtyard house.

| Building | Footprint | Height | HP | Capacity |
| --- | --- | --- | --- | --- |
| Corner building (`corner_building`: a building on a street corner) | 7 × 7 cells | 10.5 m | 1100 | 6 |
| Workshop (`workshop`: a low workshop) | 7 × 6 cells | 6 m | 800 | 4 |
| Apartment block (`apartment_3f`: three floors) | 10 × 6 cells | 10.5 m | 1400 | 8 |
| L-shaped apartment block (`apartment_l`: the footprint includes the inner yard; the door is the yard gate) | 12 × 10 cells | 10.5 m | 1800 | 10 |
| Office block (`office_block`: a tall office block) | 8 × 8 cells | 13 m | 1600 | 8 |
| Kiosk (`kiosk`: a small street kiosk; flimsy) | 2 × 2 cells | 3 m | 150 | none |

Town buildings, fourth batch (2026-10-25; a first tuning, decisions 408 to 416). Five civic and industrial buildings are
garrisoned like the homes, with the same garrison numbers and one door on the front row centre (only the capacity
differs); the market stalls, the town water tower and the cell tower are plain neutral props like the kiosk. All eight are
neutral, cost 0, build time 600 ticks, building armour. The whole footprint blocks movement, the school's yard, the fuel
station's forecourt and the factory's yard included. The clinic's and the warehouse's art door lies inside the footprint,
so their engine door (a cell just outside) is not where the art's `door_01` is.

| Building | Footprint | Height | HP | Capacity |
| --- | --- | --- | --- | --- |
| School (`school`: the footprint includes the walled yard; the door is the yard gate) | 12 × 10 cells | 8 m | 1600 | 10 |
| Clinic (`clinic`: a two-floor clinic) | 8 × 7 cells | 7.5 m | 1200 | 6 |
| Warehouse (`warehouse`: a steel portal-frame shed) | 12 × 8 cells | 8 m | 1400 | 8 |
| Fuel station (`fuel_station`: a canopy and a small shop; the height is the canopy top) | 10 × 8 cells | 5.5 m | 700 | 3 |
| Factory (`factory`: a brick hall; the chimney reaches 18 m, the height is the roof) | 14 × 10 cells | 9 m | 2000 | 10 |
| Market stalls (`market_stalls`: two rows of stalls; flimsy) | 8 × 6 cells | 3.5 m | 200 | none |
| Town water tower (`water_tower_town`: a concrete shaft with a tank) | 4 × 4 cells | 18 m | 600 | none |
| Cell tower (`cell_tower`: a lattice mast) | 3 × 3 cells | 30 m | 400 | none |

Farm buildings and a town filler (task 2, 2026-10-24; a first tuning, decisions 323 to 330). The farm shed and the house
over a shop are garrisoned like the homes, with the same numbers and one door on the front row centre. The other four
hold nobody: plain civilian structures, placed neutral, that block their footprint, take damage (building armour) and,
at 0 HP, fall like a town building (the rubble keeps blocking). Low dry-stone field walls are a map `paint` layer
(`wall`), not structures.

| Building | Footprint | Height | HP | Capacity |
| --- | --- | --- | --- | --- |
| Farm shed (`farm_shed`: a barn / animal shed, one big front door) | 8 × 5 cells | 5.5 m | 600 | 4 |
| Long farm barn (`farm_barn_long`: a limestone barn, one big double door; decision 707) | 15 × 4 cells | 4.8 m | 900 | 6 |
| Greenhouse (`greenhouse`: a plastic tunnel; flimsy) | 10 × 4 cells | 3 m | 150 | none |
| Hay store (`hay_store`: an open-sided roof on posts over hay bales) | 6 × 4 cells | 5 m | 300 | none |
| Chicken coop (`chicken_coop`) | 3 × 3 cells | 2 m | 100 | none |
| Farm water tower (`farm_water_tower`: a tank on legs) | 3 × 3 cells | 12 m | 400 | none |
| House over a shop (`house_over_shop`: a shop with a shutter and awning, flats above) | 6 × 5 cells | 9.5 m | 1000 | 6 |

- **Neutral until occupied.** A building belongs to whoever is inside and turns neutral when empty; nobody enters one held by the enemy. Infantry (workers too) walk to the nearest door and go in while there is room; "Exit" lets a soldier out at the door nearest the clicked cell, "Evacuate" empties the building.
- **Damage stages** follow HP: stage 0 above 80%, then 60%, then 35%, then above 0, and rubble at 0 HP. Capacity per stage is 100 / 100 / 75 / 50 percent; the excess is thrown out, losing a share of max HP. The rubble keeps blocking pathing.
- **Firing from inside.** Occupants fire from the surviving window firing points with their own weapons, with +2 cells of range and +4 of vision measured from the building's edge nearest the target. They can't move and aren't targetable (attacks target the building). Buildings don't block line of sight yet.
- **Bullets barely scratch buildings** (building armor); explosive warheads (rockets, explosives: the RPG and the rocket truck) hurt the building and every occupant takes **25% of what the hit would do to him**. This is by design: explosives are the counter to garrisons, even though they can empty a garrison before the building's damage stages matter, so capacity ejection and collapse survivors will be rare in real fights.
- **Collapse.** At 0 HP each occupant survives with a 50% roll (the match generator, in ID order) at 30% HP and is put out at the doors.
- **Camera.** Buildings stay solid. Units hidden behind a building show as a flat team-colored silhouette (only units the player can actually see); a building fades under the pointer only while placing something or when the player holds a key.
- **Presentation.** Dust, debris and smoke at each stage change; stages 2 to 4 smoulder (a little fire in stage 3 and in ruins); occupancy shows as pips above the roof in the holder's team color. An enemy-held building shows only "held by enemy" and its stage.


### Turned buildings (2026-10-25)

The owner asked for it ("yes of course add rotation"). The first real map, `zuberec_01` (game main d0870a7), had to face
every building south because the engine could not turn the buildings it spawns from a town file. That dropped about 90
homes (every house on the south side of an east-west street), held rowhouse rows to 2 and made the cooperative's long barns
warehouses. Every town building, home, farm building, stone village house, garrisonable block and prop, and the
observation post, now stands at any of the four quarter turns. Decisions 479 to 487.

- **The turn.** `Unit.Quarter`, 0 to 3: how many quarter turns clockwise (seen from above, north up) the archetype's layout
  stands turned. 0 is the layout as content gives it, the front (the doors) to the south; 1 the front to the west; 2 north;
  3 east. At 1 and 3 a non-square footprint stands h x w (`Unit.Turned`). Content does not change: footprint and doors stay
  as authored, doors from the unturned top-left cell.
- **What follows it.** Everything that reads a placed footprint (`World.FootW` / `FootH`, as for built sites since the
  footprint rotation): the cells a neutral building blocks for good (its rubble too), routing, fog, aim points, the spatial
  index, the occupants' cover. The doors (`World.DoorCell`, `World.LayoutCell`): soldiers walk to the nearest door and go in
  within `enterRange` of it, `Exit` puts one out at the door nearest the clicked cell, the stage ejections and the collapse
  put them out at the doors. A factory's rally on the building goes to its front. A sandbox spawn checks the footprint as it
  will stand.
- **Placing one.** The cell is always the top-left cell of the footprint as it stands.

  | Front faces | `Command.Facing` (Build, SandboxSpawn) | `World.Spawn` quarter | Footprint |
  | --- | --- | --- | --- |
  | south (as laid out) | 0 | 0 | w x h |
  | west | 8 | 1 | h x w |
  | north | 16 | 2 | w x h |
  | east | 24 | 3 | h x w |

  The facing is the turn of the layout, not the direction of the front: the front looks to facing + South
  (`World.FrontFacing`), as a tunnel's mouth does (`TunnelMouthFacing`), and as the Build command has documented since the
  footprint rotation ("0 = the front to the south").
  - *Sandbox* (`SandboxSpawn`, how the client places a town file's buildings): `Command.Facing` 0, 8, 16 or 24; any other
    facing places nothing. Without a facing (0) a building stands as before. Its unit facing stays South, as before.
  - *Spawn API*: `World.Spawn(def, cell, facing, quarter)`, facing the unit's facing as before. `World.Spawn(def, cell,
    facing)` is quarter 0: a structure stands as laid out whatever the facing (scenarios spawn a side's depot facing East or
    West), as before. A quarter outside 0 to 3, or a turn for a unit that is not a structure, a factory, a tunnel entrance,
    an airfield piece or a bridge, throws `ArgumentOutOfRangeException`.
  - *Built*: the Build facing as before; the site's quarter is facing / 8. The observation post may now be built at all four
    facings (garrison doors no longer stop a turn: `World.CanTurn` is "no production, no tunnel").
- **Not turned.** Factories (the vehicle garage, the military garage, the helipad): their doors, bays, exits, exit lanes and
  service doors are laid out to the south, so `CanTurn` refuses them and a site keeps quarter 0 (at facing 16 one stands as
  laid out, as before). Tunnel entrances, airfield pieces and bridges keep their own facing rules (their `Facing` is their
  layout's turn) and quarter 0. The howitzer's crewman still gets out at the middle of its south edge (it has no front).
  Lobby army entries have no turn (their structures are depots).
- **For the client (job R1b).** Draw a building's model turned `Unit.Quarter` quarter turns clockwise seen from above about
  the footprint centre (`Unit.Pos`): yaw -90 degrees per quarter in the map format's convention (+ anticlockwise seen from
  above, world +Z south). Read the footprint from `World.FootW` / `FootH` and the doors from `World.DoorCell(u, i)`, never
  from the archetype's `FootprintW` / `FootprintH` and `Garrison.Doors` plus the origin. A town file's pads before the world
  exists: `World.Footprint(archetype, facing)`. A town file entry takes `"facing"`: 0, 8, 16 or 24 (missing: 0), passed to
  `SandboxSpawn` as its facing.
- **For the map generator.** A front-row door (x, h) of a w x h layout with the footprint's top-left cell at (X, Y) as it
  stands: quarter 0 (X + x, Y + h); 1 (X - 1, Y + x); 2 (X + w - 1 - x, Y - 1); 3 (X + h, Y + w - 1 - x)
  (`World.LayoutCell(archetype, origin, quarter, x, y)`, `World.TurnCell`). The engine adds no check that a door's cell is
  open ground, as before; the generator keeps its own.
- **State and hashing.** `Unit.Quarter` is hashed when it is not 0, beside `Unit.Turned` and `Unit.Facing` as before, so a
  game without a turned structure hashes exactly as before: `BuildingRotationTests.OldTownsWithoutAFacingPlayAndHashExactlyAsBefore`
  pins the hashes of a town placed the old way (sandbox spawns without a facing, direct spawns facing East, West and South, a
  post built at facing 0, squads going in) to the values main 5f7a725 gave. The golden replay is byte-identical (its only
  structures are the two depots). A site built at a facing other than 0 hashes its quarter, so such games hash differently
  from before (no stored replay has one).
- **A long barn (added, decision 707).** The art landed and `farm_barn_long` is in content as proposed below (height 4.8 m from
  the art's 4.75). Before that, content had no long farm barn: the farm shed is 8 x 5 cells (16 x 10 m) and the cooperative's
  sheds on `zuberec_01` (five rows of two, the game repo's `tools/terrain/make_zuberec.py`) stand in as warehouses (12 x 8,
  24 x 16 m). The cooperative needs one. Proposal, waiting for its
  art: `farm_barn_long`, 15 x 4 cells (30 x 8 m), garrisonable like the farm shed (the farm kit's only garrisoned barn), one
  door on the front row centre `[7, 4]`, a first tuning of capacity 6 and 900 HP, height from the art. Along the barn road
  (north-south) it would stand at quarter 1 or 3, its door toward the road.
- **Tests.** `BuildingRotationTests`: every building placed neutral at all four quarters (footprint cells blocked and the ring
  open, routing, door cells and the open ground beyond them, a soldier from behind going round and in by a door and out onto
  it, collapse at the door and rubble on the turned cells), the long panel block as a wall at every quarter, the observation
  post built at the four facings, sandbox spawns turned and refused, the spawn API's refusals, a rally on a turned front, a
  turned town hashing the same twice and unlike the unturned one, the quarter hashed where the footprint is the same, and the
  old town's pinned hashes. `FootprintRotationTests` as before, the post now allowed to turn.

## Roads, props and terrain

A city asphalt kit (straight, cross, T, corner, end and two crater pieces; an 8 m two-lane carriageway between 2 m raised sidewalks) lays streets between buildings, with decorative props (streetlights, poles, burnt cars, concrete barriers, sandbags) and craters. Terrain is a warm sand ground with gravel, rock blocks on blocked cells and scattered shrubs and stones for scale. A supply depot is a sandbag-and-gabion compound under a camouflage net with stacked crates; it has a destroyed variant.

### Road kinds and speed (2026-10-24)

On a map with terrain a road is no longer just cheaper pathing: each road cell has a **kind** that sets how fast each
class of mover goes on it and what the pathfinder pays for it (`content/terrain/rules.json`, per class `roadSpeed` and
`roadCost`, in the order highway, paved, gravel, dirt). The owner called speed by road type "essential"; the numbers are
the brainstorm's examples, a first tuning (decisions 331 to 336). Roads still block nothing and raise no ground; the
look (the client's `paint` ribbons, trails and dust) is client-only, with no spotting.

| Kind (map `roads[].kind`) | What it is | Wheeled speed / cost | Tracked speed / cost | Foot speed / cost |
| --- | --- | --- | --- | --- |
| `highway` | four lanes between towns | 140% / 45% | 110% / 70% | 100% / 90% |
| `paved` (default) | town streets, rural roads, village lanes, airfield pavement | 125% / 50% | 110% / 70% | 100% / 90% |
| `gravel` | quarries, military sites, farms | 110% / 57% | 105% / 73% | 100% / 90% |
| `dirt` | tracks across fields and hills | 100% / 63% | 100% / 77% | 100% / 90% |
| `alley` | an old-town street of stone paving and steps | blocked | blocked | 100% / 90% |
| `footpath` | a worn earth path across fields and ridges | 100% / 100% | 100% / 100% | 100% / 90% |

- **Speed** is a percent of the same cell off-road and multiplies the slope and forest speed (a paved road up a gentle
  slope through scrub: 75% x 85% x 125% for a wheeled vehicle). A road never opens ground a class can't cross. Wheeled
  covers the three wheeled classes (light 4x4s, vehicles, heavy trucks). Tracked vehicles gain less on roads; off-road
  they already lose less than wheels where there is a penalty (forest 95 / 75% against 85 / 60%, slopes), and open flat
  ground has none. Infantry: no change (a soldier is no faster on asphalt; crowds already route as if roads weren't there).
- **Path cost** follows the speed: each kind costs paved's percent times paved's speed over its own, so paved keeps the
  50% (wheeled) and 70% (tracked) it always had and a vehicle takes a faster road when the detour pays (over 89 cells a Humvee
  leaves a dirt track for a highway 10 rows to the side, not for one 35 rows away: `RoadKindTests`). Costs are whole numbers on open ground's 10, so on
  flat ground wheeled pay 4 / 5 / 5 / 6 and tracked 7 on every kind.
- **Where roads cross** the faster kind keeps the cell, whatever the order the map lists them in.
- **Old maps:** a road without a kind is paved, hashes as before and routes as before; wheeled vehicles now drive 25%
  faster on it and tracked 10% (intended).
- **Renderers** read a cell's kind with `TerrainMap.RoadKindAt(cell)` to pick tyre marks and dust per surface.
- **The foot kinds** (`alley`, `footpath`) are the last two rows of the table: see "Alleys and footpaths" below.

### Alleys and footpaths (2026-10-25)

Two more road kinds for the map's `roads` list, both about infantry (decisions 388 to 396, a first take to confirm). Neither
has values of its own in `rules.json` (its `roadSpeed` / `roadCost` stay four values: highway, paved, gravel, dirt).

- **`alley`:** a narrow old-town street of stone paving and steps. **Only the foot movement class enters it.** For every
  other ground class (light wheeled, wheeled, heavy wheeled, tracked) an alley cell has path cost 0 and speed 0: a wall, like
  rock, with no special case anywhere (the cost grids carry it, so HPA* sectors, flow fields, the clearance blocks of wide
  vehicles and movement's hard cells all keep vehicles out). A vehicle never gets a route through one; with no other way round
  its goal ends short of the buildings, as for any unreachable goal. For foot an alley is a paved road with no speed change
  (decision 334 keeps: a soldier is no faster on asphalt) at the foot class's paved cost (90%).
- **`footpath`:** a worn earth path across fields and ridges. It **blocks and slows nobody**: vehicles treat it as plain
  ground (cost and speed exactly the cell beside it; a tank's run along or across one is tick-for-tick the run on bare field,
  `RoadKind2Tests`). The foot class pays its paved road cost (90%) on it and goes no faster, so a route follows a footpath
  when the detour is small (a squad took one 1% longer than the straight way for 93% of its ticks; an 18% detour it ignored).
  Unlike the other kinds the preference holds for groups: the routing of a group of soldiers costs roads as the ground beside
  them ("crowds ignore roads"), and a footpath is the one kind it does not undo. A lone soldier's own route is searched with a
  deliberate slack (a few percent), so it takes to a footpath for part of the way.
- **Where kinds overlap** the vehicle road (highway, paved, gravel, dirt) wins the cell, whatever the order the map lists them
  in, so a footpath or an alley crossing a paved road changes neither its speed nor its passability; an alley beats a footpath.
  `SetRoadKind` (a bridge's road, a script) lays exactly the kind it is given and `SetRoad` (an airfield's pavement) lays paved,
  each replacing whatever foot kind was on the cell.
- **Storage:** the flags byte is full, so a map with an alley or a footpath carries a second byte per cell (`_flags2`): bits 0-1
  the kind (0 none, 1 alley, 2 footpath), bits 2-7 spare for later per-cell flags. It is allocated only on such a map and is in
  the terrain's digest (so the world hash) only when some cell has a kind: every other map hashes exactly as before (pinned in
  `RoadKind2Tests`).
- **Placement:** an alley is not on the grid (infantry walk it), so the places that put things down ask `World.IsAlley(cell)`:
  no structure goes on one (`CanPlace` and so builders and the skirmish AI, the sandbox's spawns, airfield pieces, tunnel
  entrances, bridge sites: `World.BridgeSiteRefusal`, so the map fails to load); a ground vehicle spawned (`World.Spawn`:
  scenarios, the sandbox) or produced on one stands on the nearest cell that is not an alley, by rings like the water rule; a
  soldier stands, and lays a mine, in an alley as anywhere. `World.Spawn` of a structure itself stays unchecked, as for water
  (a map's own buildings are the author's to place; once they stand they block their cells on the grid for infantry too).
  Footpaths restrict nothing.
- **For renderers:** `RoadKind.Alley` (5) and `RoadKind.Footpath` (6), appended to the enum (the others keep their numbers);
  `TerrainMap.RoadKindAt(cell)` returns them where no vehicle road covers the cell, `IsFootOnly(cell)` (alleys), `World.IsAlley`;
  `TerrainMap.Road(cell)` stays the vehicle roads only. The look (stone paving, steps, a trodden strip) is client-only.
- **Code and tests:** `Sim/TerrainMap.cs`, `Model/TerrainRules.cs` (`MoveClass.EntersAlleys`, `RoadCostOf` / `RoadSpeedOf`),
  `Content/TerrainLoader.cs`, `Sim/WorldRoads.cs`, `Sim/Pathing/NavGrid.cs` and `Hpa.cs` (a footpath or an alley crossing a
  sector edge gets its own portal for soldiers); `RoadKind2Tests`.

## Water (2026-10-24)

Rivers, lakes and the sea are part of a map's terrain (the owner: simple for now, cool animations on the client, **no
swimming**, a bomb or shell in water must look realistic, no crater). Decisions 337 to 345, a first take to confirm.

- **In the map file** (`water`, after the roads): a river is a centre line of cells, upstream first (it flows in that
  order), `width` cells wide (default 3; a point may carry its own, `[x, y, w]`, the width changing evenly between
  points); a lake or the sea is a `polygon` in map coordinates (cell corners: cell (x, y) spans x to x + 1). Each body has
  one flat surface `height` in tenths of a metre, as pads. A river that falls is several reaches, each its own body.
  Up to 255 bodies; where they overlap the later one holds the cell.
- **Storage:** bit 6 of the cell flags is water, bit 7 a deck over it (the bridge hook); a map with water adds one byte a
  cell (the body's ID, 1-based in the list's order) and the bodies themselves. All of it is in the terrain's digest; a map
  without water takes nothing more and hashes exactly as before.
- **Shores:** a cell is water when its centre lies in the water or on its edge. A river of odd width w covers w cells
  across a straight stretch, an even width w + 1: the sim's water is never narrower than the drawn water, so no unit
  stands in it on screen.
- **No swimming:** open water costs 0 (impassable) for every ground class and is `Terrain.Water` on the world's grid, so
  routing (HPA* sectors, flow fields), movement and everything that asks the grid for a free cell keep ground units out:
  build sites, the sandbox's spawns (a structure on water is refused, a unit goes to the nearest dry cell), mines,
  scenario armies, units getting out of vehicles, factories and helicopters. A move order to the far bank of a river
  with no way round ends on the near side (as for any unreachable goal); with a way round, units take it. Aircraft are
  unaffected.
- **The surface:** over water `HeightAt` is the water surface where the bed lies below it, so sight lines and fog cross
  water flat at its surface, shells and bombs go off on it, wrecks hit it and aircraft keep their height above it. The bed
  is `GroundHeightAt` (the corners), for the client to draw under the water.
- **Blasts in water:** no crater (not even the bunker buster's deep crater) and no ground change; the splash still
  hurts whatever is in reach (a unit on the bank; later one on a bridge). The impact events carry `Water`: `Detonation`,
  `BombImpact` (its `Crater` None, `Altitude` the surface), `WreckImpact` (a crashed aircraft: a splash and it sinks, no
  burning wreck), `StrafeBurst`, `KamanEvent` (blast); `HeliCrash.Water` is a helicopter crash-landing in water: it
  sinks and nobody gets out. `World.InWater(point)` answers for any other impact (a drone's strike, a rifle round). A
  supply pallet landing in water is lost.
- **For renderers:** `TerrainMap.HasWater`, `WaterBodies` (kind, surface, the river's line and widths or the polygon),
  `Water(cell)`, `OpenWater(cell)`, `WaterIdAt(cell)`, `WaterBodyAt(cell)`, `WaterSurfaceAt(cell)` and `WaterFlowAt(cell)`
  (a river's direction along the nearest segment of its line, length 1; zero on still water).
- **Bridges:** `World.SetDeck(cell, on)` lays a deck over a water cell: it costs what the same flat dry cell would (a road
  on it too) and is clear on the grid, no longer open water; lifting it takes it back. The bridges lay and lift it ("Bridges").

## Bridges (2026-10-25)

The owner (2026-10-24, the roads and water brief): bridges are **destructible**, **can be repaired**, **can't be built** by
players, **no weight limit**; every piece stands alone. Three types, their sizes fixed with the art (1 cell = 2 m); decisions
373 to 387, a first take to confirm.

| Archetype | Footprint (w across x h along the road) | Lanes | Road on it | HP | Repair, one builder (fall to open) |
| --- | --- | --- | --- | --- | --- |
| `bridge_stone` (a stone arch, the village bridge) | 3 x 8 cells (6 x 16 m) | 1 | `paved` (the village lane) | 2,500 | 30 s (12 s) |
| `bridge_concrete` (a concrete beam bridge) | 5 x 12 cells (10 x 24 m) | 2 | `paved` | 3,500 | 45 s (18 s) |
| `bridge_metal` (a steel truss, the highway's) | 9 x 18 cells (18 x 36 m) | 4 | `highway` | 4,500 | 60 s (24 s) |

- **What it is:** a structure with a `bridge` block (`road`, `stages`, `reopen`, `helpers`; `BridgeSpec`), building armour,
  neutral for good (owner 0, nobody's; not the `neutral` flag, which blocks a footprint). Its footprint's long side `h` is the
  road (the art's glTF Z): as laid out the road runs north-south; placed facing East or West it stands turned and the road
  runs east-west (`Unit.Turned`). Four axis directions only: the placement system turns structures by quarters.
- **On the map** (`bridges`, after the water): `{"kind": "concrete", "cell": [x, y], "dir": "ns"}`: the archetype
  `bridge_<kind>`, the top-left cell of its footprint as it stands, `dir` `ns` (the road north-south) or `ew`. The world puts
  them up when the terrain is attached (`TerrainMap.BridgeSites`, `World.AttachTerrain`), each with the definition of the
  first player whose faction lists it (both factions list the three). A site must have both ends (the rows across the road
  at each end of its long side) on dry land, its road's centre line crossing water, the whole footprint on the map, its dry
  cells free of rock and buildings and no other bridge or structure overlapping; otherwise the load fails
  (`TerrainLoader.LoadMap(path, rules, content)` / `CheckBridges`: `ContentException`; `AttachTerrain`:
  `MatchSetupException`). The sandbox spawns one with the command's facing only where it can stand (`World.BridgeRefusal`,
  `CanPlaceBridge`). Players never build one: content refuses a bridge with `construction`, so `Build` places nothing.
- **Its road and deck:** every footprint cell carries the bridge's road kind (the map's road under it gives way;
  `TerrainMap.SetRoadKind`), so speed and path cost by road kind apply on it ("Road kinds and speed"); trees on it are cleared.
  Its cells over water are its deck (`BridgeState.DeckCells`, `World.SetDeck`): while the span stands they are flat (grade 0,
  whatever the carved bed under them), clear on the grid and cost what flat dry ground with that road would. The sim keeps the
  deck at the water surface (`HeightAt` unchanged); the client draws it 2.5 m above the bank.
- **It blocks nothing:** no box for movement, no block for routing or the grid; no other structure goes on it (the sandbox,
  `CanPlace`). Shots and dives pass over it onto what stands on it or the deck (like an airfield's pavement); a shot or a guided
  bomb at the bridge itself hits it. No weight limit: every ground class crosses every bridge (the widest vehicles need 3 cells,
  the stone bridge's width).
- **Targets:** nobody targets it on his own (it is neutral); players order attacks on it (ground units, aircraft, artillery) and
  blasts hurt it like a building: shells, rockets, bombs, a drone's charge, splash from a blast in the water beside it. A bunker
  buster brings it down (and digs no deep crater on its deck).
- **Stages** (`Unit.Stage`, the art's `stage_0` to `stage_3`): 0 above 70% of max HP, 1 above 40%, 2 (heavy, still passable)
  down to 0 HP, 3 the span down. Every change is a `BridgeEvent` (`Stage`).
- **The fall:** at 0 HP the span falls (`BridgeState.Down`, stage 3): the deck is lifted (open water again; the ramps and
  abutments on the banks stay ground), and every ground unit standing on a deck cell goes into the water and dies with everyone
  inside it (a vehicle's driver and riders; a mine; a helicopter landed there): nobody swims, nobody rolls to bail out. Units
  whose path crosses the span have it cut short of the water; a unit on its own route asks for a new one, a group follows its
  re-planned flow (routing sees the deck's cells change: HPA* sectors, flow fields and regions refresh). The ruin stays at 1 HP:
  never removed, no target for orders (an attack order on it ends or is refused), but blasts still hurt it (setting a repair
  back). `BridgeEvent` `Collapsed` lists the units lost (`Lost`) for the debris and the splash.
- **Repair:** builders (workers, the bulldozer; engineers have no builder) of **either side** repair any bridge with the Repair
  order (the airfield pieces' repair, `RepairCommand`): one builder restores the whole bar in the build time
  (`repairable.fullPercentOfBuild` 100: 30 / 45 / 60 s), a second adds half, a third nothing (`bridge.helpers`). A fallen span's
  deck is laid again at 40% of max HP (`bridge.reopen`): from a fall one builder takes 12 / 18 / 24 s, then it stands damaged
  (stage 1) and the repair goes on to full. `BridgeEvent` `Reopened`.
- **For renderers:** `World.Bridges` (every bridge, by ID), `World.BridgeAt(cell)`, `Unit.Bridge` (`Down`, `Repairing`: builders
  worked on it this tick, the `repair_scaffold`; `DeckCells`), `Unit.Stage`, `Unit.Turned` (the road east-west),
  `World.BridgeEvents` (`Stage`, `Collapsed` with `Lost`, `Reopened`), `TerrainMap.Deck(cell)`.
- **Code and state:** `Sim/Systems/BridgeSystem.cs` (after the tick's damage and repair, before cleanup), `Sim/Bridge.cs`;
  `BridgeState` (span down, being repaired) hashed only for bridges, the map's sites in the terrain digest only when present:
  games and maps without bridges hash as before. No new CommandKind. Tests: `BridgeTests`.

## Trees as obstacles (2026-10-25)

The owner (2026-10-25): standing tree trunks are real obstacles for vehicles, which steer **between** trees; infantry pass
freely. **Tanks and big trucks push trees over, but only at low speed**; jeeps and light trucks can't and must go between.
A pushed tree **falls the way the pusher drives**: its fallen trunk **blocks where it lies** while the cell where it stood
**opens**; the fallen trunk **stays about 30 s, then disappears**. Decisions 439 to 450: the owner accepted the defaults on
2026-10-25 (night run), still open to tuning.

- **The trunks** are worked out from the forest, never stored in a map (`Sim/Trees.cs`, `TreeTrunks`). The map is cut into
  2 x 2 blocks of cells (slots); each slot's integer hash (`TreeTrunks.SlotHash(sx, sy)`, a fixed salt, pinned in
  `TreeTrunkTests`) picks one of its 4 cells and a roll from 0 to 99, and a trunk stands in that cell when the roll is under
  the trunk chance of the cell's forest density (`trees.trunkChance` in `content/terrain/rules.json`: 0 / 0 / 50 / 85 % for
  none / scrub / grove / dense; scrub is bushes only; the first take was 35 / 70). So a grove has a trunk in about 12.5% of
  its cells (a tree every 32 m^2) and dense forest in 21.25% (every 19 m^2), never two in one slot. The same hash places each
  trunk off its cell's centre (up to 0.31 of a cell either way, inside the cell) for the client. One slot per trunk rather
  than a chance per cell spaces the trees as trees space themselves and gives a sharp line between passable and not. At the
  first take (35 / 70), a flood fill of the 2 x 2 clearance over random layouts (run once in Python for this choice, not in the
  repo): on 80 x 80 cells of grove a jeep (clearance 2 cells) got across in 30 of 30 layouts, of dense forest in 0 of 30, where
  a per-cell chance giving dense forest the same 17.5% let it across in 8 of 30 (decision 439). At 50 / 85, the engine's own
  routing for the jeep's class (`wheeled`, clearance 2) over 30 windows of 80 x 80 cells of the real trunk layout, each walled
  off so the route stays inside (a throwaway test, not in the repo): grove 21 of 30 (29 of 30 at 35 / 70 by the same run), dense
  forest with the cars' block lifted 0 of 30 at both. So a jeep no longer always gets through 160 m of grove between the
  trunks; in open country it goes round (an open question for the owner).
- **Storage:** the flags byte is full, so a bit array: one bit a cell for the standing trunks, worked out when the map loads
  (only on a map whose rules have trunks), and from the first fall one bit a cell for the fallen trunks lying: 0.25 byte a cell
  at most (6,000 x 3,000: 4.5 MB). Neither is in the terrain's digest: the forest density that gives the trunks is, and the
  world hashes what fell (`World.Stumps`, `FallenTrunks`, `TreePushes`), so a tree falling never recomputes the map's
  digest. Ticks never hash: the bits are read.
- **What a trunk is to each class** (`trunks` per movement class; decision 440): `ignore` (foot: nothing, standing or fallen),
  `avoid` (wheeled and light wheeled: a wall) or `push` (tracked and heavy wheeled). Dense forest is shut to the cars
  outright (decision 450): its trunks leave them no way across, only pockets a crowd wedges in; a grove they cross between
  the trees. The terrain folds it into its per-class
  cost grid: 0 for a class that avoids trunks, the cell's cost times `pushCost` (300%) for a pusher (decision 441). So routing
  needs no special case: the clearance widths decide by themselves who fits between which trees (a jeep needs a 2 x 2 block
  clear of trunks), and movement's walls (a cell its class can't enter: `PushOut`, the wall distances) keep a jeep's body out
  of every trunk's cell. A trunk blocks its whole cell (2 m), as rock does: routing and movement agree on that, so nothing
  wedges between what routing allows and movement refuses; a jeep stops up to about 0.8 cell short of the drawn trunk.
- **Pushing** (decision 442): after ground movement, in the same phase (`Sim/Systems/TreeSystem.cs`), every pusher that
  drove this tick and whose body overlaps a trunk's cell ahead of it (the cell's nearest point in front of its heading, or
  under its centre) presses on it; bodies in ID order, so a trunk's pusher is the lowest ID on it. A trunk is not a wall to
  a pusher: it drives on into it at no more than `pushSpeedPercent` (30%) of its speed from the next tick (the tree leans
  under its front), and its push counts as progress for stuck detection. After `pushTicks` (30, 1.5 s) of contact in a row
  the tree falls; contact lost, the push starts over.
- **The fall** (decision 443): the way the pusher faces, snapped to 8 directions (`TreeTrunks.DirOf`). The stump's cell opens
  for good (`World.Stumps`); the trunk lies from the next cell along that direction for `fallenLengthCells` (4) cells, fewer
  where the map ends, a building, rock or water stops it. It blocks those cells for `fallenTicks` (600, 30 s: decision 444)
  for every vehicle but the classes with `crossFallen`, then it is gone and they are clear.
- **Pushers roll over fallen trunks** (`crossFallen` true for tracked and heavy wheeled; decision 445, an owner question):
  slowly, at their push speed and cost. A tree falls the way its pusher drives, so a pusher that can't cross fallen trunks
  lays a wall across its own way at every push. Measured on a 64 x 64 map, a tank sent through a 20-cell band of dense forest
  (`TreeTrunkTests` layout): with `crossFallen` false it gave up at x 29.7 after felling 11 trees (781 ticks; a 32-cell band
  the same); with true it was through in 606 ticks after 4 (769 ticks and 7 for 32 cells), never stuck. A heavy truck: 1,867
  ticks and 15 trees against 949 and 3. Jeeps and light trucks never cross one.
- **Under a falling trunk** (decision 446): nobody is hurt (a later task adds it). A vehicle whose body is in its cells is
  pushed out like from any wall the next tick; infantry don't mind it. Ground units on their own route whose way ahead crosses
  it ask for a new route (the bridges' rule); a group re-plans its flows once routing sees the change.
- **Undergrowth:** `forestCost` and `forestSpeed` are the undergrowth between the trunks. Dense forest no longer stops the
  pushers (`forestBlockedFrom` gone from both): tracked 180% cost and 60% speed there, heavy wheeled 280% and 35% (decision
  447); it now stops the cars (decision 450). Infantry keep theirs.
- **Routing refresh:** a fall and an expiry rebuild only their cells (the stump and the trunk's, through the terrain's change
  log): routing refreshes the rectangle round them (its clearance reach, 13 x 9 cells for an east-west trunk), the HPA*
  sectors and regions over it, movement's wall distances round them (`AFallRefreshesRoutingOnlyRoundIt`). Never the map.
  Only the classes whose costs a change altered lose their sectors, regions and flows (`NavGrid.LastChanged`, a bit a class):
  a tree falling is nothing to infantry, so a squad walking through the wood keeps its flow
  (`AFallLeavesTheRoutingOfClassesItDoesNotChangeAlone`); fog ignores the cells only a trunk changed
  (`TerrainMap.ChangedSince(..., sightOnly: true)`); movement's wall distances redo the box round any number of changed cells
  (a whole-map pass only when the box is most of the map; it was any tick with more than 4,096).
- **Clearing** (decision 448): whatever clears forest for something built on it (construction sites and their ring, bridges,
  airfield pieces, tunnel entrances) clears through `World.ClearTrees(cell)`: the cell's forest (its trunk with it) and every
  fallen trunk across it, taken away whole (`TreeEventKind.Cleared`). A stump stays a stump while the cell keeps forest
  (`TerrainMap.SetForest` keeps a felled tree felled). A tunnel entrance clears only its footprint and its apron, not the
  blend ring its shaped ground eases over (decision 458), and the trees do not grow back when it is gone (decision 459); in
  dense forest that cars cannot enter, a medium entrance's cleared strip is a pocket for them (decision 462; `TunnelForestTests`).
- **Vision** is unchanged: forest density drives sight lines and canopy as before; trunks and falls change no sight.
- **No goal behind a fallen trunk** (decision 449): a jeep whose way is shut (dense forest, a trunk across the only gap) goes
  as near as it can and its order ends there, as for any unreachable goal; it does not wait for the trunk to go. A player's
  next order after it is gone gets through (`AFallenTrunkBlocksAJeepForItsTimeThenItPasses`).
- **For the client:** `TerrainMap.HasTrunks`, `HasTrunk(cell)` (draw a tree there), `TerrainMap.TrunkOffset(cell)` /
  `TrunkPoint(cell)` (where in the cell, raw), `Fallen(cell)`, `ChangedSince(version, cells)` (the cells whose trunk or fall
  changed); `World.FallenTrunks` (`FallenTrunk`: `Stump`, `Dir` 0 north clockwise to 7, `Length`, `ExpiresAt`, `By`,
  `Cause`; `CellAt(k)`, `Covers(cell)`), `World.Stumps`, `World.TreePushes` (`Cell`, `Pusher`, `Ticks`),
  `World.TreeEvents` (`TreeEvent`: `PushStarted`, `Fell`, `Expired`, `Cleared`, with the stump, direction, length, the
  unit, the cause and where the push came from); `TreeTrunks` for the hash, slots, offsets and directions.
- **Code and tests:** `Sim/Trees.cs`, `Sim/Systems/TreeSystem.cs`, `Sim/TerrainMap.cs` (the layers, the costs and speeds),
  `Model/TerrainRules.cs` (`TrunkMode`, `MoveClass.Trunks` / `PushCost` / `PushSpeedPercent` / `PushTicks` / `CrossFallen`,
  `TreeRules`), `Content/TerrainLoader.cs` (checked: unknown fields are errors, every class says `trunks` once the rules
  have a `trees` block, push values only for a pusher), the movement system's push speed; `TreeTrunkTests`.
- **Performance** (M3 Pro, Release, the same machine and session, before = `c69caa0`): memory, the standing trunks' bit
  layer, 0.125 byte a cell (the fallen layer another 0.125 from the first fall): `-terrain-bench` 16.01 to 16.13 layer bytes
  a cell. Ticks never hash trees; the tree step reads the bodies movement collected. `-move-bench forest,group500 -map flat`
  (no forest there): forest p50 / p99 0.40 / 0.74 ms both, group500 0.34 / 0.43 to 0.34 / 0.42. `-move-bench forest` on the
  1,500 x 1,500 bench map (`make_heightmap.py --size 1500 --bench-layout`: 500 soldiers and 100 technicals across a grove
  with dense patches): p50 / p99 0.40 / 1.79 to 0.46 / 1.84 ms; done at tick 3,801 instead of 4,870 (the technicals go round
  dense forest rather than crawl through it), worst overlap 20% to 8%. `-terrain-bench 1024 -battle 200 -fog true`: battle
  tick mean 1.80 to 1.79 ms, worst 258 to 250 ms (the first far order's routing). `-terrain-bench 2048x1536 -battle 200` (the
  coming valley map's size): load 258 to 266 ms, battle tick mean 1.88 to 1.95 ms, worst 424 to 460 ms (the first far order's
  routing; the trunks add portals: 10,584 to 11,117).
- **The defaults at 50 / 85** (the night run of 2026-10-25; measured on simcore e5e2afe against this change, M3 Pro,
  Release, a loaded machine; the probes marked "throwaway" are not in the repo):
  - *Do the pushers avoid dense forest? No, but every tree costs them more.* A tank and a heavy truck sent across 20 cells of
    dense forest on 9 lanes (9 trunk layouts) arrive on all 9 (`TanksAndHeavyTrucksStillCrossDenseForestAtTheRulesDensity`).
    Mean ticks, tank / heavy truck: no trunks 403 / 769, at 35 / 70 574 / 882, at 50 / 85 593 / 933 (+3% / +6%); trees pushed
    over per lane 4.2 / 4.0 and 5.7 / 6.2 (throwaway). In a grove (20 cells, 9 lanes) 358 / 609 with no trunks, 401 / 678 at
    35 / 70, 484 / 718 at 50 / 85.
  - *The planner prices them higher, so a near gap now wins more often.* A pusher pays `pushCost` 300 for a cell with a trunk,
    and a clearance-3 body touches one in nearly every step of dense forest at 85. Route cost across a 40-cell belt of dense
    forest, mean of 30 lanes (the same route over open ground costs 1,065): heavy truck 1,821 with no trunks, 3,160 at
    35 / 70, 3,597 at 50 / 85 (+14%); tank 1,401, 2,278, 2,571 (+13%). With a 4-cell dense belt and a 3-row gap (tried at gaps
    2 rows apart), a heavy truck goes through the belt once the gap is 32 rows off or more (26 at 35 / 70, 16 with no trunks),
    a tank at 22 (18, 8): one layout, so about. `CliffsBlockEveryoneAndDenseForestTheCars` had the gap 17 rows off, where the
    belt cost the truck 960 against a detour of 982 at 35 / 70 and 1,017 at 50 / 85; its gap is 42 rows off now. Push cost
    and push speed are unchanged. Proposal if the owner wants the trucks to push on rather than detour: `pushCost` 250 puts
    the 40-cell belt at 3,197 for a heavy truck and 2,305 for a tank (35 / 70 at 300: 3,160 and 2,278); only the planner's
    price was measured at 250, not the crossing time or the gap distance.
  - *Jeeps and groves:* see "The trunks" above: a jeep crosses 21 of 30 windows of grove of 80 x 80 cells, 29 of 30 at 35 / 70.
  - *Cost to the tick* (`SimRun -move-bench all -map` on the 1,500 bench map, tick p50 / p99 ms, e5e2afe then this change):
    idle10k 3.16 / 5.05 then 3.23 / 5.49, moving10k 12.03 / 18.06 then 11.30 / 20.53, group500 0.43 / 0.56 then 0.43 / 0.59,
    group2000 2.35 / 2.84 then 2.31 / 2.60, pass 8.12 / 10.64 then 7.93 / 10.72, forest 0.47 / 0.60 then 0.46 / 0.64, depots
    4.74 / 6.08 then 4.65 / 5.68: the same within the machine's noise (load average 3.5 from other jobs). Overlap max moved
    in two workloads: moving10k 11% to 8%, depots 19% to 42% (ticks at 10% or more 222 to 658, stuck max 11 to 38; the
    worst overlap is beside forest cells in `MOVEBENCH_DEBUG=1`'s map); the others are equal (idle10k 0, group500 3, group2000 18, pass 36,
    forest 8 to 9). `-move-bench forest` alone: tick p50 / p99 0.47 / 1.83 then 0.49 / 1.85 ms, done at tick 3,801 then 3,797.

### Strikes blow trees down (2026-10-25)

The owner (2026-10-25, the strikes list, items 1 to 3; fire is later): blasts knock trees down by warhead size, trunks falling
**away from the impact**; falling trees hurt infantry; the cleared lanes open routes. Decisions 451 to 457, a first take to
confirm.

- **Which blasts, how far** (decisions 451 to 453): the terrain rules' `trees.fell` lists the warheads that blow trees down,
  each with a percent of its weapon's splash radius; the radius is never under `trees.fellMinRadius` (1.5 cells: an anti-tank
  missile has no splash). Every standing trunk whose point lies within it falls. The list lives with the terrain rules, not the
  warheads, so flat games (and the golden replay) are untouched; the world checks its names against the content when the
  terrain is attached (an unknown warhead or tag: `MatchSetupException`). One hook: `Explode` (every splash: shells, rockets,
  bombs, mines, crashes, a drone's charge) calls `FellByBlast`, and a detonation without splash (a missile) calls it itself.

  | Warheads | `fell` | Trees per impact in dense forest (grove), mean of 16 |
  | --- | --- | --- |
  | `atgm`, `rocket`, `heli_missile`, `explosive`, `at_blast`, `heavy_cannon` (under 1.5 cells: the minimum) | 100 | Kornet or RPG 1.0 (0.6), 0 to 3 |
  | `mortar_bomb`, `he_mpat`, `air_strike`, `mrl_rocket`, `heavy_rocket`, `bunker_buster`, `crash` | 100 | mortar 3.4 (1.8), tank HE 5.3 (2.8), howitzer 6.3 (3.4) |
  | `drone_charge`, `kaman_charge` | 60 | |
  | `heavy_missile`, `ballistic`, `bomb` (half the splash: 2.5 to 6.5 cells) | 50 | 500 lb 3.4 (1.8), heavy bomb 20.6 (9.9), strategic bomb 23.4 (11.3) |
  | bullets, cannon (30 mm), autocannon, darts, air-to-air missiles, the anti-personnel mine | none | 0 |

  A strategic bomber's 20-bomb carpet over dense forest blew down 139 trees in a lane (`SimRun -spike-bench carpet`). The table
  and the 139 are at the first take's trunk chances (35 / 70); at 50 / 85 the counts rise by the chances' ratio (dense forest
  x 1.21, a grove x 1.43): the carpet blew down 172 (same bench, same session, simcore e5e2afe for the 139).
- **The fall** (decision 454): away from the impact, the 8-way direction from the impact point to the trunk's point (north if
  the blast is exactly on it), then exactly a pushed tree's: the stump opens, the trunk lies 4 cells for 30 s, a wall to the cars
  (a trunk across a road shuts it the same: `ATrunkBlownAcrossARoadBlocksAJeepForItsTimeThenItPasses`). The `Fell` event's
  `Cause` is `Blast`, `From` the impact point, `By` the shooter (the unit whose shot it was, 0 for a mine or a crash).
- **Falling trees hurt** (decision 455): wherever a trunk falls (pushed too), each unit with one of `trees.fallDamageTags`
  (infantry) whose position is on one of its cells loses `trees.fallDamage` (10) percent of its max HP, once, in ID order: not
  soldiers inside a vehicle or a building, not vehicles (a later task). A soldier already under 10% of his HP dies.
- **Lanes** (decision 456): a felled cell opens for vehicles at once (routing's refresh), so a bombed wood gets lanes the cars
  can use once the fallen trunks are gone. Forest density stays as it was, so **sight does not change** (sight reads the
  density; the spec's "sight lines change" waits for fire, task C, which drops a burnt cell's density).
- **Performance** (decision 457; M3 Pro, Release, `SimRun -spike-bench carpet-nofell,carpet` on 512 x 512 cells of dense forest
  with 300 soldiers walking across the bombs' line and 38 technicals, the order to 100 ticks after the last bomb, the fourth of
  four runs): without felling tick p50 / p99 / max 0.169 / 0.269 / 0.544 ms; with it (139 trees) 0.165 / 0.312 / 2.265 ms;
  with fog 0.229 / 0.580 / 0.596 against 0.229 / 0.588 / 2.420 ms. Before routing kept the classes a change does not alter, the
  same carpet cost p99 6.2 ms and up to 12 ms a tick: every bomb re-planned the soldiers' flow across the wood. A tick's falls
  are put in order once, at its end; the work is the cells within the radius. At 50 / 85 the same carpet blows down 172 trees
  and runs p50 / p99 / max 0.166 / 0.315 / 2.199 ms (fourth of four runs, fog off; e5e2afe, 139 trees: 0.162 / 0.302 /
  2.144 ms; without felling 0.164 / 0.215 / 0.524 ms, e5e2afe 0.192 / 0.276 / 4.982).
- **Code and tests:** `FellByBlast`, `HurtUnder`, `ResolveTrees` in `Sim/Systems/TreeSystem.cs`; `TreeRules.Fell` /
  `FellMinRadius` / `FallDamagePercent` / `FallDamageTags`; `TreeFallCause.Blast`; `TreeBlastTests`.

### Concealment from the air (2026-10-25)

The owner (2026-10-25, the trees spec, all approved): today aircraft and drones see over everything; under the trees they
should see only up close, or when a unit moves or fires, and better with the right sensor. Ground units keep seeing about 2
cells into dense forest. Content `content/mvp/concealment.json` (the table) and archetype or loadout `sensors`; code
`Sim/Systems/ConcealmentSystem.cs`, hooked into `VisionSystem.cs` and `Fog.cs`; tests `ConcealmentTests` (62 cases). Every
number is a starting value; the owner's are marked (owner), the rest are decisions 464 to 478 to confirm.

| From the air, a unit in | still | moving | firing |
| --- | --- | --- | --- |
| scrub | seen (owner) | seen (owner) | seen |
| grove | within 3 cells (owner); a vehicle 6 | within 8 (owner); a vehicle 16 | revealed 5 s (owner) |
| dense | hidden from cameras (owner); a vehicle within 4 | within 4 (owner); a vehicle 8 | revealed 5 s (owner) |

- **Where it applies** (decisions 464, 465): with fog of war on a map with terrain, to the units that move on the ground:
  infantry (`foot`) and vehicles (`wheeled`, `tracked`), standing out on the map. Structures (hidden tunnel entrances and
  stations too), traps, aircraft parked on the ground and units inside a vehicle, building or tunnel keep the rules they had.
  Without fog nothing changes (ground sight ignores forest there as well), so flat games and the golden replay play as before.
- **How** (decisions 466, 477): a cell is cover when its density limits any camera reach (grove and dense). A unit standing on
  cover is spotted on the fog grid only by what is not an air viewer (the grids count those stamps apart, per cell; the canopy
  itself still shows as seen from the air on `FogOf`), and each air unit looks at it in the vision system's distance pass with
  its sensors, by level distance from the point under the air unit, within its vision. The look is staggered like every look
  (every 4 ticks or on a new cell), and what it saw stays spotted until its next look, so a unit is found or lost up to 3 ticks
  late. **Moving** is moving last tick (`Unit.Vel` not zero; decision 467).
- **Vehicles hide less well** (owner; decision 468): twice the infantry's reaches, and still in dense forest within 4 cells.
- **Firing** (owner: about 5 s; decision 469): every shot (`Unit.LastShot`, any weapon) shows the shooter to every air sensor
  as in the open for the next 100 vision passes, then the canopy hides it again. Only to air units: ground sight is unchanged,
  and it is not the howitzer's flash (which shows a gun to every enemy). Measured: spotted on each of passes 1 to 100 after
  the shot, hidden from pass 101 (lost by the eye's next look) (`TheRevealLastsFiveSeconds`).
- **The Hide stance** (owner; decision 470): `Stance.Hide` (5; `SetStance`, content `"stance": "hide"`) holds fire exactly as
  Hold fire does in every system (no auto target, no Guard-order chase, no retaliation call, air defence holds; attack and
  attack-ground orders still fire). A soldier at the edge of dense forest with an enemy in reach fires on Guard and is seen by
  a camera 10 cells away; on Hide (or Hold fire) it holds and stays hidden; on Hide, ordered to attack, it fires and is seen.
- **Sensors** (archetype `sensors`; a loadout's own `sensors` replace them in that mode; decisions 471 to 473):
  - **camera** (every air unit's default): the table, plus a low helicopter's bonus;
  - **thermal** (the Overseer, the Watcher, the gunship: owner): under any canopy a vehicle that moved in the last 30 s
    (`Unit.MovedAt`, its engine warm) and moving infantry in scrub and groves, as in the open; never moving infantry in dense
    forest beyond the camera's 4 cells, never still infantry;
  - **ground radar** (the stealth multirole's heavy mode, its ground mode: owner): moving vehicles under any canopy, as in the
    open; never infantry or a parked vehicle, even with its engine warm. In stealth mode it has a camera only;
  - the Orlan (scout drone, owner), the jets, the bombers and the helicopters have a camera.
- **Low helicopters** (owner: "a bit further"; decision 474): a helicopter at most 40 m over the ground (30 m in transit, 15 on
  a gun run, 5 hiding; popped up to 60 m it is not) adds 2 cells to every limited camera reach (a grove's still soldier at
  5 cells instead of 3); hidden stays hidden. The attack helicopter's mast radar (and the H1 radars) still spot vehicles under
  any canopy, parked or not (decision 475).
- **Lost under the trees** (decision 476): an enemy an air unit spotted that walks under canopy drops out of `SpottedBy` and
  `SeenBy` the normal way (by the eye's next look). The engine keeps no last-seen positions: the client remembers where and
  when it last saw each enemy, as it remembers building ghosts.
- **For renderers (job D2).** `World.AirCoverOf(unit)` → `AirCover(Hidden, CameraReach, RevealedUntil)`: `Hidden` is the leaf
  marker (under grove or dense canopy, not revealed by its own fire: an enemy camera sees it only within `CameraReach`, zero
  never); `RevealedUntil` the tick a shot's reveal ends (0 otherwise). False and zero without concealment, in the open or scrub,
  and for structures, traps, aircraft and units inside anything. Enemy last-seen markers: record `Unit.Pos` and `World.Tick`
  each tick an enemy is `World.IsSpotted` (or `IsVisibleTo`) by the player, and keep the marker once it is not.
  `World.SensorsOf(unit)` (`AirSensors` bits) for an aircraft card, `Stance.Hide` for the stance buttons, `Unit.MovedAt`
  (engine warm) if wanted.
- **State and hashing:** `Unit.MovedAt` (hashed when not 0; kept only where concealment applies); the rest is read from hashed
  state (`Vel`, `LastShot`, `Loadout`, `Altitude`) and the terrain. The per-cell ground counts are a pure function of the units
  like the grids. Content: the block hashes only when present; `sensors` only when more than a camera (decision 478: the
  block lives in `content/mvp`, so the golden replay was re-recorded for the content hash only; its commands are unchanged).
- **Performance** (Release, M3 Pro; `SimRun -bench-cover 40`: 300 Resistance soldiers and vehicles on 256 x 256 cells in
  stripes of open ground, scrub, grove and dense forest, a third walking, with fog, without and with 40 Overseers and Watchers
  over them; three interleaved runs each of the old and the new build): the vision system's mean without drones 0.074 ->
  0.082 ms, with the drones 0.532 -> 0.560 ms (p99 0.85 -> 0.91 ms). Counting the ground stamps apart rather than the air ones
  keeps the drones' big, frequent sweeps as they were (the air-count first take cost 0.632 ms). `-bench 500` (flat, no fog)
  is unchanged within noise (vision 0.015 to 0.14 ms by workload, both builds).

### Forest fire (2026-10-27)

The owner (2026-10-25, the strikes list, ideas 4 to 8, all approved; additions for C: "fire must not lag", "normal airstrikes
make minimal fire", the incendiary bomb later spreads far more): burning trees burn 20 to 40 s and end as black snags, burnt
cells drop a forest level; fire spreads between neighbours by density with a cap; burning forest smokes like a screen; infantry
in it take damage and leave on their own; vehicle routes avoid it; burnt ground is ash. Content `content/mvp/fire.json`, the
warhead `fire` block (`warheads.json`), the terrain rules' per-class `fireCost` / `thinnedForestAsBefore` and
`trees.blastThins`; code `Sim/Systems/FireSystem.cs`; tests `FireTests` (26). Decisions 500 to 514, all to confirm.

- **What burns** (decision 500): a forest cell (scrub, grove or dense; not open ground, water or a building's cell), its
  trunk with it. A cell burns once: a rolled 400 to 800 ticks (20 to 40 s), then it is burnt, glows 30 s (embers) and is ash
  for good, one forest level down (dense to grove: the cars may enter it, sight reaches further, the canopy hides less). A
  trunk there stays standing, a black snag, still a trunk to every rule (decisions 501, 504).
- **What lights it** (decisions 505, 506): a blast of a warhead with a `fire` block, from the same hooks that fell trees and
  after them, so a bomb lights the trees round its crater: the forest cells within `radius` or `radiusPercent` of the splash,
  the cells with a standing trunk first, then the nearest, at most a rolled `count`. The `bomb` warhead: 1 or 2 within its
  splash radius that never spread ("minimal fire"); shells, rockets and missiles light nothing. Left out, `count` means every
  cell within the radius: the hook for the incendiary bomb (C3: a wide radius, `spreads`, `spreadPercent` above 100, and `ground`:
  open ground burns too; "Incendiary bomb" below) and the hypersonic missile (H1: `radius` 40, `spreads`). `CommandKind.SandboxIgnite` (sandbox only) lights a cell for demos.

  | Warhead | Cells lit per impact | Spreads |
  | --- | --- | --- |
  | `bomb` (500 lb, 2,000 lb, heavy, strategic, supersonic, stealth, glide, F-35 bombs; the gunship's 105 mm and big round) | 1 or 2 within the splash radius | no |
  | every other warhead (shells, rockets, missiles, bullets, mines, drones) | 0 | |

- **Spread** (decisions 502, 503): no wind. Once a second (staggered by cell index, so the looks spread over the ticks) a
  burning cell that spreads lights each of its 8 neighbours that can burn with `spread` per mille by the neighbour's density
  (scrub 6, grove 40, dense 80; corners at `diagonal` 50%), times its spread percent (which the cells it lights keep: decision 541). Over a 30-s burn a side neighbour catches
  about 1 in 6 in scrub, 7 in 10 in a grove, 9 in 10 in dense pine (`FireSpreadsMostInDensePine...`: 64 two-cell islands per
  density). At most `maxBurning` (150) cells burn at once on the whole map and spreading lights at most 6 a tick; at the cap
  nothing more catches and the fronts wait for cells to burn out. Every roll is an integer hash of the match seed, the cell and
  the tick: fire never draws from the world's generator.
- **Smoke** (decision 507): a burning cell not under a screen that lasts past its next look pops a smoke-system screen round
  itself (`Source` 0, `SmokeCloud.Fire`; 4 cells, 20 s), at most 16 at once: it blocks sight and breaks missile locks exactly as
  the Humvee's screen does, so a forest fire works as a smoke screen.
- **Units** (decision 508): every second a soldier (`damage.tags`: infantry) standing in a burning cell loses 10% of its max HP
  and, unless already walking to a cell not burning, walks to the nearest one within 8 cells (its order is replaced); a
  vehicle parked in a cell that catches drives out the same way, unhurt. Units inside vehicles, buildings and tunnels are safe.
- **Routes** (decisions 509, 510): the terrain gets the fire in batches every `terrainEveryTicks` (40, 2 s): the cells lit since
  become walls to every vehicle class (`fireCost` 0), the cells burnt out lose the wall and a forest level. Routing refreshes
  only the cells round them and the classes they changed, and a group re-plans its flows once a batch; units on their own route
  whose way ahead crosses a newly walled cell ask for another every 10 ticks. Infantry routes ignore fire (`fireCost` 100: they
  walk out when it burns them), and a burnt or blasted cell keeps its old forest's cost and speed for infantry
  (`thinnedForestAsBefore`): with infantry walls and forest drops every batch rebuilt the infantry routing round the moving
  front (HPA* sector rows), about 15 ms a batch on the bench below.
- **Blasted cells open sight** (decision 511, the follow-up of 456): a cell whose tree a blast blew down loses one forest level,
  once (`trees.blastThins`): sight lines through a bombed wood open; pushed trees thin nothing. `World.BlastedCells`.
- **State and hashing** (decision 512): `World.Fires` (sorted by cell), `BurntCells` (in burn-out order), the batch not yet
  applied and `BlastedCells` are hashed (`World.Hash` and `HashReference`), only once one exists. The terrain's bits (burning,
  burnt, walled, dropped, thinned) are not; its digest reads a burnt or thinned cell's forest as it was, so fire never
  recomputes it.
- **For renderers.** `World.Fires`: the burning cells (`Fire`: `Cell`, `Start` tick, `Length` ticks, `Ends`, `SpreadPercent`,
  `By`): draw flames on the cell and its trunk (`TerrainMap.HasTrunk`, `TrunkPoint`) from `Start` to `Ends`. `World.FireAt(cell)`:
  `None`, `Burning`, `Burnt` (dark ground with embers: the first 30 s), `Ash`; `World.FireOf(cell)` (the `Fire` there or
  null); `World.BurntCells` (`BurntCell`: `Cell`, `EndedAt`) for the ash layer; `World.IsSnag(cell)` (a black snag stands:
  burnt and its trunk still up; a pushed or blasted snag becomes a stump as any tree). `World.FireEvents` (last tick: `Ignited`
  with its length, the shooter and whether it spreads; `BurntOut`; `Cooled`; each with `Snag`: a trunk stands there).
  `World.Smokes` with `SmokeCloud.Fire` (fire smoke: draw it from the burning cells under it) and `SmokeEvents` (`Popped` with
  `Unit` 0). `TerrainMap.Burning`, `Burnt`, `Thinned`, `Forest` (the density after fire) and `ChangedSince` (the cells whose
  costs or forest the batches changed). `World.FireRulesInPlay` for the ember time.
- **Performance** (M3 Pro, Release; `SimRun -bench-fire 8 -maxticks 2400 -out fire.json`: 1,024 x 1,024 cells of forest in 8 x
  8 blocks, about half dense, 300 Resistance soldiers and technicals walking east through the middle with fog of war, 8
  spreading fires lit round the centre, against none; each run twice, the second reported; `bench/fire_8.json`): the fire
  system itself (burn-outs, spread, smoke, burns: the active list only, no map scans) p50 / p99 / max 0.0012 / 0.0086 / 0.021
  ms a tick, at the cap (150 burning) for 1,698 of the 2,400 ticks, 488 cells burnt. The whole tick p50 / p99 0.213 / 0.409 ms
  without fire, 0.228 / 1.194 ms with it; the difference is routing (the paths phase p99 0.22 to 0.87 ms: the walking groups
  re-plan round each batch of walls) and fog (vision p99 0.12 to 0.15 ms); 7 ticks over 2 ms against 4 (the 3 over 10 ms in
  both are the first far order, cold routing: max 124 ms). With infantry walls and forest drops (`fireCost` 400, no
  `thinnedForestAsBefore`) the same bench over 1,200 ticks ran p99 16.1 ms (26 ticks over 10 ms: about one each batch). `-spike-bench carpet` (20
  bombs over dense forest: 172 trees blown down, their cells thinned, the bombs' fires lit): p50 / p99 / max 0.167 / 0.322 /
  1.727 ms (fourth of four runs; before fire 0.166 / 0.315 / 2.199).

### Incendiary bomb (2026-10-27)

The owner (2026-10-25): the medium jets (the multirole, an F-16 type; the stealth multirole, an F-35 type; the air superiority
jet, an F-22 type) get **one incendiary bomb**; dropped on an area it **starts a fire there** and spreads far more fire than
normal strikes; **a direct hit kills infantry outright**, the edge and round it take **heavy damage**, vehicles heavy damage too;
it **will be an upgrade** of the jet. The upgrade system is a later job (C5), so for now the bomb sits behind an upgrade switch
that no shipped lobby turns on. Content: `warheads.json` (`incendiary`), `components/sentinel.json` (`incendiary_bomb`), the three
jets in `archetypes/sentinel.json`. Engine (generic, opt-in): weapon `upgrade` and `pointsOnly`, warhead `maxHpBySplash`, the fire
block's `ground`, `Player.Upgrades` / `Unit.Upgrades` / `World.GrantUpgrade` (`Sim/Systems/UpgradeSystem.cs`),
`CommandKind.SandboxUpgrade` (44), the demo lobby `content/setups/incendiary_demo.json`. Tests `IncendiaryTests` (17 tests, 19
cases). Every number is a starting value: decisions 533 to 546.

- **The switch** (decisions 533 to 535). The bomb's weapon names an upgrade (`"upgrade": "incendiary"`): a unit without it carries
  none (`Unit.StockMax` of that stock is 0, so it starts and rearms empty and every system looking for ammunition passes it by),
  the jet's other weapons and loadouts exactly as before. A player's upgrades (`Player.Upgrades`, bits of
  `ContentSet.Upgrades`) come from the lobby (a player's `"upgrades": ["incendiary"]`; none in `battle.json`, `sentinel.json` or
  the golden battle) and from `World.GrantUpgrade(player, name)`: **the hook the upgrade system will call** (bought where, cost and
  time are C5's), which also marks every unit the player has (`Unit.Upgrades`; a unit made later gets its owner's), so a per-jet
  upgrade later needs no new state. A grant loads nothing at once: the bomb fills at the jet's next rearm (a station's service or
  a loadout change). The sandbox grants one with `CommandKind.SandboxUpgrade` (`Command.Unit` the name; ignored in a real match);
  the client's demo lobby `content/setups/incendiary_demo.json` (the Sentinel lobby as a sandbox) starts player 1 with it.
- **One extra bomb** (decision 533): on top of each jet's bombs (not a swap): the multirole with either bomb preset, the air
  superiority jet with its glide bombs, the stealth multirole in both modes (each loadout's `ammo` lists it: 1).
- **Ground points only** (decisions 536, 537; weapon `pointsOnly`): an attack-ground order (or a line) drops it, an attack order
  on a unit and Guard's hunt never do. The multirole and the air superiority jet have no other bomb with fire modes, so it is
  their one bomb slot, selected: **an attack-ground order drops it on the first pass while it is aboard**, then their other bombs
  (or the gun) on the same point. On the stealth multirole it is the second bomb weapon: its heavy bombs stay the default, and the
  bomb weapon picker (`CommandKind.SetBombWeapon` 1, `World.BombWeaponsOf`) takes the incendiary for ground points; selected, an
  attack on a unit still drops the heavy bombs, and once it is dropped the heavy bombs come back. The picker refuses a bomb weapon
  the jet does not carry (no upgrade: `Unit.Carries(w)` greys the button).
- **The blast** (decisions 538, 539, 543): two rings about 30 m across, all of it within **4 cells** and **60%** out to **7.5
  cells** (`splashInner` 40, `splash` 75, `splashOuterPercent` 60), as **shares of each target's max HP** (the warhead's
  `maxHpPercent`, falling off with the splash: the new `maxHpBySplash`), directional armour aside (fire wraps a hull):

  | Target (armour) | Inner ring (to 4 cells) and a direct hit | Outer ring (to 7.5 cells) |
  | --- | --- | --- |
  | infantry | **killed** (100%) | 60% |
  | light vehicles (the technical, the cargo trucks, the dirt bike, the stations) | **destroyed** (100%) | 60% |
  | trucks and launchers, the Humvee (`medium_vehicle`) | 70% | 42% |
  | armour (the tanks, the infantry carrier) | 40% | 24% |
  | a parked aircraft | destroyed | 60% |
  | buildings | 500 damage (the weapon's) | 300 |

  Measured (`TheCentreKillsInfantryAndLightVehiclesAndTheEdgeHurtsHeavily`): soldiers at 0 and 3 cells dead, at 6 cells 40% left,
  at 9 untouched; a technical at 2 cells destroyed (its driver inside unhurt), at 6 cells 40% left; a light rocket truck 30% /
  58% left; an Abrams 60% at 3 cells, 76% at 7.1, untouched at 8.5. The bomb: 1 a jet, the multirole's 35-degree dive delivery
  and scatter, fire mode `single`, a small runway crater, **strike tier 3** (500), no resupply but a station's rearm; it fells no
  trees (not in `trees.fell`).
- **The fire** (decisions 540 to 542; the warhead's `fire` block: `radius` 60, `spreads`, `spreadPercent` 300, `ground` 240):
  every forest cell within **6 cells** (about 113 cells, inside the blast and under the cap) catches at once, spreading at **3x**
  the fire rules' chances; the cells its fire lights keep its percent (C1's spreading lit them at 100: the front stays fierce;
  normal fires, at 100, spread exactly as before). The **open ground** within the 6 cells (land without forest, roads too; not
  water, decks or buildings) burns too: a **ground fire of 12 s** (`ground` 240 ticks, fixed) that hurts soldiers in it (10% a
  second, they walk out), walls vehicles off (routes go round it, a parked one drives out), smokes and lights the forest beside
  it; burnt out it is **scorched ground** (burnt, then ash: it never burns again; no forest to drop). The cap (`maxBurning` 150,
  6 spread ignitions a tick) holds for both. Measured on 160 x 160 cells: over dense pine both an incendiary's fire and a normal
  spreading one run at the cap (672 and 686 cells burnt in 2 minutes); over scrub, where a normal fire dies out, 436 cells
  against 131. A real drop on a squad at a wood's edge (`ARealDropKillsTheSquadSetsTheWoodAlightAndIsDeterministic`): all 8
  soldiers dead, 473 cells burnt or burning after 80 s, 71 of them scorched open ground.
- **State and hashing** (decision 544): `Player.Upgrades` and `Unit.Upgrades` are hashed when not 0 (`World.Hash` and
  `HashReference`), the lobby's `upgrades` are in the setup digest only when present; ground fires are ordinary `World.Fires`
  entries (already hashed; that one is open ground is the terrain's). Content: the new fields hash only when set.
- **For renderers.** **The drop**: `World.BombReleases` and `BombImpacts` as for every bomb (`BombImpact.Warhead` "incendiary",
  `Tier` 3); the card: `World.BombWeaponsOf(u)` (the F-35 lists the heavy bombs then the incendiary), `Unit.BombWeapon`,
  `Unit.Carries(w)` (false: grey it, no upgrade), its rounds in `Unit.Charges[w.Stock]` of `Unit.StockMax(w.Stock)`; whether the
  player has it: `Player.Upgrades` (bit `ContentSet.UpgradeIndex("incendiary")`). **The impact flash**: the `BombImpact` (a
  fireball the size of the 7.5-cell blast, not a crater: `Runway` small only on a runway) and the same tick's `FireEvents`
  `Ignited` (`By` the jet) over the 6-cell disc. **The ground-fire patches**: `World.Fires` entries whose cell is open ground
  (`World.IsGroundFire(cell)`; `Fire.Start` the impact tick, `Fire.Length` 240, `Fire.Ends`): flames on the bare ground or road
  from `Start` to `Ends`, then `FireAt` `Burnt` (embers 30 s) and `Ash` (scorched, `BurntCells`); their smoke is the fire smoke
  (`SmokeCloud.Fire`). **The wide fire front**: the forest cells are forest fire as before (`Fire.SpreadPercent` 300 on the
  incendiary's cells and every cell its front lights: draw it fiercer, taller flames), up to the cap.
- **Performance** (decision 546; M3 Pro, Release; `SimRun -bench-incendiary 4 -maxticks 2400`: 1,024 x 1,024 cells all dense
  forest, 300 Resistance soldiers and technicals walking east through the middle with fog, 4 incendiaries going off round the
  centre at the start, against none; each run twice, the second reported; `bench/incendiary_4.json`): the fire system p50 / p99
  / max **0.0011 / 0.006 / 0.035 ms** a tick, at the cap for 2,306 of the 2,400 ticks, 535 cells burnt; the whole tick p50 / p99
  0.198 / 0.337 ms without, 0.203 / 0.522 ms with (paths p99 0.16 to 0.24 ms: the walking groups re-plan round the walls); the
  3 ticks over 10 ms are in both (the first far order, cold routing: max 108 to 109 ms). C1's bench is unchanged (`-bench-fire 8`:
  fire p50 / p99 / max 0.0012 / 0.008 / 0.024 ms, 488 burnt, 1,698 ticks at the cap). `-bench 100,500,1000` (flat, no jets, no
  fire) the same within noise before and after (tick p99 0.27 to 2.8 ms by workload, both builds).
- **Not yet**: the upgrade itself (C5: where it is bought, cost, time, per jet or side-wide; `GrantUpgrade` is its hook); no
  skirmish AI use; the client part (the bomb, the fireball, the burning patches, the fierce front).

## Effects direction

Effects follow a Generals reference and are the brightest thing on screen.

- **Explosions** are short and sharp (over in about half a second), red-orange with flame tongues; yellow-white only at the core, never washed out. MRL blasts are bigger area ground blasts.
- **Smoke** is thin, see-through gray-brown and reads as one coherent thing: never thick black blobs and never visible circles (a ray-marched volume today).
- **Lasting damage is on the ground**: burnt-ground decals, not dark low smoke. Burning wrecks burn for 20 s from the centre in multiple shades, then smoulder.
- **Area of effect** shows as a barely visible dome and ground ring (about 1%).
- Planned: simulated flipbooks (fire and smoke baked from a physical simulation to animated textures) replace the procedural effects.

## Content model

Every unit, from infantry to planes, workers and buildings, is assembled from parts instead of written as its own class. Adding content means adding data; adding a new mechanic means one new behavior.

```mermaid
flowchart TD
  A["Archetype<br/>base body: tags, locomotor, HP, speed, armor, slots"] --> P
  C["Components<br/>weapons, abilities, perks"] --> P
  F["Faction<br/>allowed archetypes and parts, rule modifiers"] -- filters --> P
  P["Preset<br/>built by the player in the lobby"] --> M["Match setup<br/>every player's presets + a hash of all content"]
  M --> U["Unit in the sim<br/>stats computed once, behaviors run every tick"]
```

### Building blocks

| Block | What it is | Defined by |
| --- | --- | --- |
| Archetype | A base body: tags, locomotor, HP, speed, armor, slots | Data file |
| Component | A part that plugs into a slot: weapon, ability or perk, with a cost and requirements | Data file |
| Preset | An archetype plus chosen components, named by the player | Lobby, saved with the match |
| Faction | Allowed archetypes and components, plus rule modifiers | Data file |
| Behavior | The code that makes one kind of component work each tick | C# system |

An archetype, a base body:

```json
{ "id": "anti_armor", "tags": ["infantry"], "locomotor": "foot",
  "hp": 55, "speed": 9, "armor": "infantry",
  "slots": { "primary": 1, "secondary": 1 } }
```

A component that plugs into a slot:

```json
{ "id": "aa_missile", "slot": "primary",
  "requires": { "tags": ["infantry"] },
  "effects": [{ "type": "weapon", "warhead": "aa_missile",
                "range": 70, "reloadTicks": 80, "targets": ["air"] }] }
```

A player's preset, built in the lobby:

```json
{ "name": "Drone hunter", "archetype": "anti_armor",
  "components": ["aa_missile", "short_rifle"] }
```

### Behaviors

New content is data only. A brand-new mechanic is one new behavior plus data.

| Behavior | Covers |
| --- | --- |
| Weapon | Guns, rockets, anti-air; which layers it can hit is data (`targets: ["air"]`) |
| Aura | Heal, repair, speed and armor buffs |
| Placer | Booby traps, mines, sensors: spawns a hidden entity |
| Trigger | A trap firing when an enemy comes in range |
| Builder | Workers raising construction sites |
| BuildAssist | Engineers speeding up construction and unlocking better building variants |
| Repair | Fixing vehicles and buildings |
| Locomotor | Foot, wheeled, tracked, hover, flying; flyers skip ground pathfinding |

### Rules that keep it deterministic

- **Stats are computed, never stored ad hoc:** base stats, then modifiers from preset, faction, auras and upgrades, in a fixed order, as integer percents.
- **Definitions load once at match start,** sorted by ID into indexed arrays; no dictionaries inside the tick.
- **The content set and every player's presets are hashed into the match setup.** Machines with different data refuse to start instead of desyncing later; mods come for free.
- **Tags and requirements, not type checks:** code asks "has tag infantry?", never "is this an Assault?".
- **Systems run in a fixed order:** commands, orders, abilities and placement, movement per layer, combat, construction, auras and effects, cleanup.

## Architecture

```mermaid
flowchart TD
  PI["Player input"] --> Q["Command queue<br/>stamped for tick N+2, same order on every machine"]
  BOT["Bot / AI"] --> Q
  NET["Network (lockstep)"] <--> Q
  Q --> SIM["Deterministic simulation<br/>20 ticks/s, integers only<br/>orders, pathfinding, movement, combat, economy, production"]
  DATA["Content data files"] --> SIM
  SIM --> ST["Game state<br/>hashed every tick"]
  ST -. "tick hash" .-> NET
  ST --> R["Godot renderer<br/>reads state, interpolates between ticks"]
  BL["Blender scripts: glb models + JSON sidecars"] --> R
```

Players, the AI and the network all reach the sim through one command queue, so replays and multiplayer reuse the same path. The renderer only reads state, so drawing can never cause a desync.

## Build order

Units first: Phase 1 gets you looking at real units and playing a battle against a simple, tunable bot before any base or economy exists. The deterministic sim core is done: the golden replay passes on Linux, macOS and Windows. Status as of October 16, 2026 is marked **(done)**, **(in progress)** or **(not started)** below.

**Phase 1: units you can see and play.** Two armies spawn; no base, no economy; win by wiping out the other army.

1. **Content model for units (done).** Classes, two-slot loadouts, vehicles and kits as data; redo the sample content for this roster.
   - Done when: the Resistance units run from data files and the golden replay passes on every OS.
2. **Art pipeline and Godot shell (done).** Soldiers and vehicles built by Blender scripts, exported as glb models with JSON sidecars and drawn by Godot in real time from the sim. (This replaced an earlier plan of pre-rendered 8-direction sprites.)
   - Done when: you watch soldiers walk and shoot as real-time 3D models.
3. **Controls and group movement (in progress).** Select, move, attack, control groups; units stop overlapping.
   - Controls are done (trackpad-first, command card, control groups). Pathfinding and group movement are known-weak and are next on the roadmap.
   - Done when: you command a squad through a gap without jamming.
4. **The full unit roster (in progress).** Four classes and loadouts, vehicles with crew, light, medium and heavy launchers, cargo trucks, drones, traps.
   - The sim runs the whole roster, garrisonable buildings and the supply depot from data. Models exist for the five infantry classes, the technical, the rocket truck, two city blocks, the road kit, the supply depot and the ammo emblem; the dirt bike, light and medium rocket trucks, cargo truck, drones and traps still draw as stand-ins.
   - Done when: every MVP unit works in a battle.
5. **Tunable test bot (not started).** A simple bot that fights you, plus headless bot-vs-bot battles with every tuning value in data files.
   - Done when: you play a battle against it, change a number, and see the result change.

**Phase 2: base and economy.**

6. **Structures and workers.** The six structures, training queues, starting defenses, win condition.
   - Done when: you build a full base from the HQ and win by razing the enemy's.
7. **Gold and oil.** Mining, carrying, depletion, derricks and refineries, supply radius.
   - Done when: a match runs on its own economy.

**Phase 3: map systems.**

8. **Fog of war.** Vision, hidden mines, trap detection.
   - Done when: a hidden mine appears only after a unit sees it.
   - Shipped 2026-10-18: per-player fog with line of sight over terrain and woods (`MatchSetup.Fog`), the skirmish AI; forest concealment of units standing in woods is still to come (`docs/vision-plan.md` in the game repo).
9. **Tunnels.** Network, travel time, collapse. Done 2026-10-18 (see "Tunnels" above).
   - Done when: killing a central entrance collapses the isolated ones.
10. **Balance runs.** Test bots with fixed strategies, 100-match batches.
    - Done when: win rates per strategy are reported. This is the MVP; LAN 1v1 comes next.

**Roadmap from here** (agreed October 16, 2026): finish buildings, roads and the supply depot with its emblem; movement and pathfinding; audio (royalty-free sound bundles); the visual-effects flipbook bake and wiring; fog of war; base and economy; the game UI (minimap, sidebar, cursors, click markers, unit voices); the first real map; tunnels; test bots and balance; LAN 1v1.

**Not in the MVP:** the second faction's heavy armor (the Sentinel has the Watcher, the Overseer and, since the night of 2026-10-19/20, two jets: "Air combat"), scout drones and drone vehicles, spotter/heavy AT skills (the Sentinel's assault medic came on 2026-10-21), hidden supply caches, buildable defenses and upgrades, player-controlled gunner seats and vehicle capture (the technical's gunner is part of the vehicle), ammo through tunnels, LAN (first milestone after the MVP) and online play, an opponent AI with difficulty levels, campaign, more maps and 2v2. Scavenging is dropped.

## Open questions

Decided: real-time 3D in Godot 4 with a C# sim library (pre-rendered sprites were tried and dropped); simcore is open source (MIT) and the game's art stays in a private repo; online play is not a launch goal before LAN works.

- [ ] Exact starting defense set per base
- [ ] Secondary slot choices for Assault and Engineer
- [ ] Salvos and rockets per launcher (playtests)
- [ ] Traps per soldier (playtests)
- [ ] Working title (the second faction is the Sentinel: a regular army; its full roster is open)
- [ ] Team size and who owns art vs code
- [ ] Whether the tactical camera preset becomes the default gameplay camera
- [ ] Whether roads get a footprint in the sim beyond speed and path cost by kind (since 2026-10-24: no blocking, no height; bridges are task 5, infantry-only alleys and footpaths task 6)
- [ ] Water: fords or amphibious units ever? Rivers whose surface falls along their length (per-point heights) instead of reaches?
- [ ] Line of sight: buildings do not block it yet
