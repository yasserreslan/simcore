using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>A diagnostic prerequisite check. Available describes last-observed friendly capabilities, not verified AI quality.</summary>
public sealed record AiCapabilityStatus(string Capability, bool Configured, bool Available, string Detail);

public sealed partial class PlayerAi
{
    /// <summary>Content/roster blockers for the standard-opponent milestone. Never feeds planner decisions or resolves hidden enemies.</summary>
    public IReadOnlyList<AiCapabilityStatus> Capabilities
    {
        get
        {
            if (_world is not { } w) { return []; }
            var production = _plan?.Production.Select(g => w.Match.Players[Player - 1].Find(w.Content, g.Unit))
                .Where(d => d is not null).Select(d => d!).ToArray() ?? [];
            var builders = _own.Any(u => Mobile(u) && u.Def.Builder is not null);
            var builderGoals = production.Where(d => d.Builder is not null).ToArray();
            var infantryGoals = production.Where(d => !d.IsStructure && d.Archetype.Flight is null && d.Crew is null && d.Weapons.Count > 0 &&
                w.Content.Tags.Contains("infantry") && d.Tags.Matches(w.Content.TagsOf("infantry"))).ToArray();
            var airGoals = production.Where(d => d.Archetype.Flight is not null).ToArray();
            var producer = production.Any(d => HasOperationalProducer(w, d));
            var supply = _own.Any(u => u.IsBuilt && !u.Rubble && u.Def.Supply is { Storage: false } s && (s.Stock == 0 || u.Stock > 0));
            var air = _own.Any(u => u.IsBuilt && u.Def.Archetype.Flight is not null) || airGoals.Any(d => HasOperationalProducer(w, d));
            return
            [
                new("reconnaissance", ScoutLimit > 0 || _rules.Foundation is { Aircraft: true },
                    _own.Any(ScoutReady) || _own.Any(u => u.Def.Archetype.Flight is not null),
                    "Ground observers must be free, healthy and unreserved; availability is a current assignment opportunity."),
                new("construction", _plan is { Construction.Count: > 0 }, builders,
                    builders ? "A mobile builder survives; site legality/reachability still governs each order." : "No mobile builder survives."),
                new("production", production.Length > 0, producer,
                    producer ? "A configured unit has a compatible operational producer; queue capacity is checked when issuing orders." : "No configured unit currently has an operational producer."),
                new("builder_replacement", builderGoals.Length > 0, builderGoals.Any(d => HasOperationalProducer(w, d)),
                    builderGoals.Length == 0 ? "No builder training goal is configured; starting workers cannot be replaced automatically." : "Replacement requires the configured builder's real production chain."),
                new("infantry_replacement", infantryGoals.Length > 0, infantryGoals.Any(d => HasOperationalProducer(w, d)),
                    infantryGoals.Length == 0 ? "No infantry-like reinforcement goal is configured; existing squads are a finite resource." : "Only declared legal production chains count; no sandbox reinforcements."),
                new("resupply", _rules.SupplyTripCells > 0, supply,
                    supply ? "An operational friendly ammunition supplier has stock or unlimited supply." : "No operational stocked ammunition supplier is currently available."),
                new("aircraft", _rules.Foundation is { Aircraft: true }, air,
                    air ? "Aircraft exist or a configured aircraft has a compatible production station." : "Aircraft policy is insufficient without aircraft or a real production/station chain."),
                new("local_reinforcement", _rules.Foundation?.ForceManagement is not null, _own.Any(Ready),
                    "Requires a nearby safe recipient, whole eligible groups, spare force capacity and a reachable objective."),
                new("tunnel_network", Doctrine?.Tunnels is not null,
                    _own.Any(a => a.IsBuilt && !a.Rubble && a.Def.Archetype.Tunnel is not null &&
                        _own.Any(b => b.IsBuilt && !b.Rubble && b.Def.Archetype.Tunnel is not null && World.TunnelLinked(a, b))),
                    "Transit still requires compatible sizes, free capacity, approach/exit access and acceptable known danger."),
                new("faction_air_operations", Doctrine?.Air is not null, air,
                    "Observed strikes require readiness and threat checks; known enemy aircraft can require airborne fighter cover."),
                new("planner_checkpoint", true, true,
                    "Format 7 includes reinforcement timing; the host separately preserves matching world and pending inputs."),
            ];
        }
    }

    private bool HasOperationalProducer(World w, UnitDef def) => !def.IsStructure && def.Strike is null && _own.Any(f => f.IsBuilt && !f.Rubble &&
        f.Def.Archetype.Production is { } p && p.Builds(def.Tags) &&
        (long)f.HP * 100 >= (long)f.Def.MaxHp * p.MinHpPercent && ProductionCompatible(w, f, def, p));
}
