# Demand-aware cargo flow

Optional `foundation.logisticsControl` replaces nearest-store cargo dispatch. The shipped AI enables it;
null retains the previous cargo executor. This is compile-only locally under owner instruction. It uses
normal delayed commands and owned state; the engine still controls real stock transfers and priorities.

## Destinations and claims

A receiving store must be a built, non-rubble friendly structure with finite supply stock. Its room is
capacity minus current stock and inbound loads. Source/receiver claims combine planner jobs and ordinary
owned LoadCargo/Unload orders, counting each truck once. An unloading truck needs room for its whole
current load; a loading truck needs a whole unclaimed refill. These mirror the engine's start conditions,
not hypothetical partial transfers. Up to two trucks can claim a store for each leg.

Receiving utility combines remaining stock deficit and nearby friendly consumers with low weapon
charges or HP. Consumer value is capped at 16; it is an urgency proxy, not proof that each consumer can
be serviced there. Ground-only stores exclude aircraft from this count. A store above the ordinary
low-stock threshold qualifies only when such demand exists. The top 16 destinations receive terrain,
actual passable endpoint/radius, travel and known-danger checks. Travel/danger cost can reorder that
shortlist. Known influence is not a route-safety guarantee.

An empty truck shortlists 16 loading sources by distance. Finite sources protect 20% of capacity and
subtract other planned loads; unlimited sources remain unlimited. The source must be legal, reachable
and safe, and there must be another useful receiver with room for a full load. This opportunity check
is cheap geometry-free screening; the later delivery leg confirms actual access. It does not guarantee
that a usable receiver will still exist after loading.

A job retains its loading source when cargo arrives and chooses a delivery leg that excludes that
source. This prevents an immediate load/unload reversal into the donor. The same absolute mission
expiry covers both legs, including waiting with a load when no receiver is currently usable. The
planner never creates stock, treats a mobile supplier as a cargo depot, or assumes future deliveries.

## Progress, review and ownership

Every decision observes cargo changes, meaningful travel improvement and active loading/unloading
countdown progress. A 400-tick stall cancels the trip; ordinary total mission expiry is still authoritative.
Reviews every 100 ticks recheck store availability, stock/room, terrain/radius and known safety. At most
four reviews and two selections run per decision. Oldest review first, then truck ID, prevents review
starvation. Idle trucks with no selection cool down for a review interval so the first two IDs cannot
consume every selection forever. Roster/claim scoring scans still scale with input size.

A successful load transitions the same reservation to delivery, retaining source identity. Delivery
completion, invalid destinations, threat, loss of commandability, stalled travel and total expiry release
through a delayed cancellation state. A commandable truck receives Stop before ownership is released;
its reservation remains for input delay plus one tick, then takes the ordinary rest cooldown. If the
command budget defers Stop, ownership remains. An uncommandable truck also waits through the input
window; it cannot be reassigned as a mobile actor. Dead/missing owned trucks release immediately.

Cancellation keeps real cargo aboard for future ordinary decisions. This increment does not reserve
physical cargo in the engine, reroute another player's inputs, split truck loads, or promise to avoid
retrying a temporarily blocked destination after cooldown. Sustained command-budget exhaustion can
still delay cancellation, just as it delays other ordinary planner commands.

## State and diagnostics

Format 11 captures source identity, last cargo/countdown, best travel distance, progress/review clocks
and cancellation release deadline per cargo job. These values affect continuation and are validated;
formats 1–10 are rejected. Policy bounds and digest include all ten logistics fields. The host must
still preserve the exact World and pending input queue. Derived claims are rebuilt from owned state.
`CargoTrips`, capability reports and SimRun traces expose active/waiting/cancelling work. No shared World
hash/state, gameplay data, private client or golden files change.

Deferred acceptance: simultaneous trucks and ordinary inputs; room/stock becoming insufficient; empty
and unlimited sources; protected source floor; loading then receiver destruction; donor reversal;
countdowns longer than stall windows; dynamic obstructions and stuck jobs; selection/review fairness;
Stop deferred by budget and input delay; exact format 11 continuation; representative large-roster cost.
No local tests, matches or benchmarks were run. Parent remote CI is evidence for its existing suite,
not the behavior added here.
