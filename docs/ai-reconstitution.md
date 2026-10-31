# Sustainable production and roster replacement

The shared production layer now supports replacing workers and infantry through the actual Resistance
barracks added on `main`. It also treats the Sentinel bulldozer as an essential replacement. This is
implementation groundwork; no local tests, matches or benchmarks were run under the owner's instruction.

## Use the lobby's real loadouts

An exact goal still names a preset or slotless archetype. Optional `archetypeFamily: true` instead names
an allowed archetype and selects among its existing compiled lobby presets/default. For example:

```json
{
  "unit": "engineer",
  "archetypeFamily": true,
  "requiredRoles": "repair",
  "excludeDrivers": true,
  "count": 1
}
```

A lobby's Mechanic can satisfy that request even if its preset has a different name. The AI does not
invent a repair loadout when the lobby only supplies sapper/drone variants. Role filters consider default
weapon mode and available compiled weapon stocks: a primary-only AA preset with a disabled rifle
secondary cannot satisfy a ground-counter role merely because its archetype is tagged as an RPG soldier.
Ground recruitment/ammunition readiness also respect each live unit's current weapon mode.

Within a family, doctrine preferences rank candidates first. Equal preferences use a stable per-battle
preset choice when variety is enabled, or definition index otherwise. The command names the selected
real preset. No simulation RNG draws or new unit definitions are created. This is preset selection,
not automatic loadout design or an optimizer for every armor matchup.

Family goals count all eligible owned variants plus queues, bays, doors and delayed production inputs.
An optional driver exclusion leaves vehicle crews out of the desired foot roster while retaining
passengers, garrisons and tunnel travelers. Changing a preferred preset therefore does not make the AI
forget the other variants it already owns. Counting is conservative during observation/input transitions;
normal queue and population checks remain authoritative.

## Keep essential replacements possible

Optional `foundation.productionControl` adds three controls:

| Control | Shipped value | Purpose |
| --- | --- | --- |
| essentialPriority | 2000 | Bonus for an unmet essential production goal, including emergency/posture allocation |
| essentialProducerBonus | 1500 | Extra construction demand when a producer serving that essential deficit is missing |
| reservedSlots | 4 | Maximum AI population headroom retained for outstanding essential deficits |

The headroom is bounded by actual deficits and only held for a usable producer or a configured construction
chain with a surviving mobile builder. Ordinary production cannot consume it; essential production can.
New construction also respects the AI population budget/headroom, except a missing producer needed to
bootstrap an essential goal may use that headroom. Resuming existing sites creates no new population.
A facility's producer-demand bonus takes the maximum applicable demand rather than whichever goal
happened to be listed first.

The population budget includes queued/unfinished/delayed production and conservatively reserves driver
slots for crewed products. These controls do not cancel useful military queues, delete units to make
room, bypass the AI cap, supply money or claim recovery is possible from a preexisting full roster.
If every builder and its replacement producer are lost, the AI cannot manufacture a new chain. A defeated
player cannot resurrect through training. Those are explicit content/world blockers.

## Shipped faction goals

Resistance builds one `resistance_barracks` and maintains two essential workers. It requests a family
of 12 foot assault soldiers, adaptive antiarmor soldiers, one repair engineer when that loadout exists,
conditional MANPADS, a mortar and a sniper family. Existing vehicles, supplies and tunnel goals remain.
Normal battle variety can slightly change eligible combat counts; builders keep their fixed target.
The engine still checks the barracks' real tags, exclusions, gate, queue, health, build time and placement.

Sentinel keeps its real vehicle/armor/air chains and requests its bulldozer as essential without the old
initial delay. No Sentinel infantry producer is invented. Starting foot troops remain finite where a
faction/lobby lacks a legal training chain or a usable preset. New units flow through the existing
crew/specialist/defense/assault/reinforcement allocation layers; training does not bypass reservations.

## Explain and continue

`ProductionPlan` and runner traces expose requested goal, selected definition, role filter, family count,
desired count, essential status, actual operational producer/station availability and population blocking.
Statuses distinguish delayed, unavailable-definition, satisfied, producer-or-station-unavailable,
population-blocked and deficit. Deficit does not promise a free queue/door this tick. Capabilities also
report unresolved family/role loadouts and actual builder/infantry replacement opportunities.

All new goal options and production controls are validated and digested. The resolver is stateless and
uses the retained battle seed/matching compiled lobby. Existing pending production/queues and probe/mission
state suffice for production; shared checkpoint format **10** also retains defense assignments, with changed
policy digests rejecting incompatible saves.
No simulation content or golden replay changes belong to this increment.

Deferred scenarios: mixed preset names and family counts; role/mode/stock filtering; drivers versus
passengers/garrisons/tunnels; delayed production and queue/door transitions; producer damage/loss/rebuild;
worker replacement near the population cap and bootstrap construction; unsupported faction/preset chains;
seed stability and exact checkpoint continuation; long matches and candidate cost at scale.

Research context: [production continuity](rts-ai-research.md#production-continuity-and-reserving-capacity).
