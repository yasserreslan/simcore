# Recovery that accounts for service and demand

Optional `foundation.combatControl.recovery` improves ground recovery without changing the World,
creating resources or adding a second assignment ledger. It is enabled in the shipped AI policy.
Null retains the preceding recovery executor. This increment is compile-only locally; behavioral
acceptance and performance evidence remain open.

## Can this provider actually help?

A supplier is valuable only for missing active weapon pools it can refill. Shared pools count once;
zero-capacity and unlimited pools need no stock. Depot-only pools require a structure supplier and use
their per-round cost. Ordinary aura resupply respects no-resupply, truck-reload, ground-only and storage
restrictions and the supplier's round cost. A finite supplier must afford at least one relevant round;
free depot rounds remain possible with empty stock. A unit cannot supply or repair itself.

Repair value requires an operational radius repair effect matching a member's tags and reaching the
planned passable point. Walking beside a garage does not start garage service, so garage-only repair
receives no aura bonus. Existing specialist jobs still issue actual Service/Rearm commands.

Bonuses scale by the fraction of the force the provider can currently help. Weapon demand aims for the
existing 65% recovery ammunition threshold; radius repair aims for the new policy's 80% HP target.
Ability kits, smoke/APS, cargo and aircraft fuel are outside this estimate. A compatible provider may
help only part of a mixed force; the planner never invents a refill for incompatible members.

## Shared demand and choosing a destination

After the existing 16-provider shortlist and terrain checks, each provider gets a soft cost of 60 per
other needy friendly unit already near it or reserved there by a returning mission. Units are counted
once in stable ID order; the current force is excluded. Existing mission provider IDs supply the
reservation ledger, rebuilt from owned state. A same-decision earlier assignment is visible to later
choices. These are soft preferences, not exclusive engine reservations or promises of stock allocation.

Estimate weapon refill costs for the recovering force and competing units. When demand exceeds a
finite supplier's current stock, add up to 1600 cost in proportion to the shortage. Unlimited suppliers
have no shortage penalty. No future cargo deliveries or economic income are assumed. The engine still
allocates real rounds in its normal order, and other ordinary commands may change demand.

Safe endpoints remain preferred over unsafe ones. A cheaper unsafe candidate cannot displace a valid
safe destination merely by exceeding the switching margin. Provider disappearance, exhausted useful
stock, containment, incompatible care, access or threat changes still trigger periodic reevaluation.
The original 100-tick recheck and switching margin remain; there is no new reservation clock.

## Arrival, care and release

At a useful provider, arrival alone no longer releases a recovering force. It retains ownership while
compatible ammunition/repair work remains, moves closer when the arrival tolerance leaves a beneficiary
outside the radius, and guards while in range. Once applicable work is done or no longer available it
can release normally. The absolute 600-tick recovery deadline, starting when recovery began, is never
extended by waiting, switching providers, damage or resupply. Unsupported care cannot keep it forever.

The subsequent cargo-flow increment uses shared checkpoint format 11: choices use existing mission objective, phase, provider ID and reevaluation
clock. Demand and compatibility are rebuilt from current owned observation. All three optional policy
fields enter strict validation and the compatibility digest, rejecting a checkpoint from a different
policy revision. No World state/hash, content/mvp data or golden files change.

## Limits and deferred acceptance

The shortlist can omit a better provider. Provider scans and the shortlisted claim scans still scale
with roster/mission size; straight threat samples are not a route search. Weapon compatibility mirrors
current supply rules and must stay aligned when the engine changes. Cost estimates ignore competing
ability kits, reload timing, actual pulse order, movement collisions, future deliveries and repair
throughput. A force may travel most of its deadline and have little time left for care. A fallback
shelter need not provide service. Whole forces remain together; there is no patient split or optimal
stock allocation.

Deferred: scarce stock with simultaneous retreats; shared/depot-only/no-resupply/truck pools; a repairer
inside a carrier; moving/destroyed providers; blocked footprints and radius-edge arrival; impossible care;
readiness without premature release; deadline and command-budget exhaustion; exact checkpoint continuation;
large-roster cost. No local tests, matches or benchmarks were run.

Source rationale: [OpenRA activity study](rts-ai-research.md#service-aware-recovery).
