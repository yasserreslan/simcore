using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private bool PreserveScout(World w, Mission m)
    {
        if (_rules.Foundation?.Reconnaissance is not { } p || m.Kind != AiMissionKind.Scout || m.Phase != AiMissionPhase.Active) { return false; }
        if (Danger(w, m.Objective, false) > p.MaxDanger)
        { WithdrawScout(w, m, p, "scout-destination-danger-changed"); return true; }
        var lead = Own(m.Members[0])!;
        var threats = _visible.Where(e => e.CanAct && e.Inside == 0 && e.IsBuilt && !e.Rubble &&
                e.Def.Weapons.Any(weapon => (weapon.Effect.Targets & Layers.Ground) != 0 && weapon.Effect.Interceptor is null && weapon.Effect.Cruise is null))
            .OrderBy(e => Distance(Cell.Of(lead.Pos), Cell.Of(e.Pos))).ThenBy(e => e.Id).Take(p.ThreatCandidates);
        foreach (var enemy in threats)
        foreach (var id in m.Members)
        {
            var member = Own(id)!;
            var threatened = enemy.Def.Weapons.Any(weapon =>
                (enemy.WeaponMode != WeaponMode.PrimaryOnly || weapon.Index == 0) && weapon.Effect.Interceptor is null &&
                weapon.Effect.Cruise is null && World.CanHit(weapon, member) && World.HasAmmo(enemy, weapon) &&
                World.Within(World.From(enemy, member.Pos), member.Pos, World.Reach(enemy, weapon) + Fixed.FromInt(p.RangeBufferCells)));
            if (!threatened) { continue; }
            WithdrawScout(w, m, p, "scout-preserve-observation");
            return true;
        }
        return false;
    }

    private void WithdrawScout(World w, Mission m, AiReconRules p, string reason)
    {
        var probe = m.Probe;
        Recover(w, m, reason);
        if (probe >= 0) { _retry[probe] = w.Tick + (uint)Math.Max(_rules.ProbeRetryTicks, p.AvoidTicks); }
    }

    private int InformationAt(World w, int probe, AiReconRules p)
    {
        var width = (w.Grid.Width + _rules.RegionCells - 1) / _rules.RegionCells;
        var height = (w.Grid.Height + _rules.RegionCells - 1) / _rules.RegionCells;
        var x = probe % width; var y = probe / width;
        var value = 0;
        for (var dy = -p.InformationRadius; dy <= p.InformationRadius; dy++)
        for (var dx = -p.InformationRadius; dx <= p.InformationRadius; dx++)
        {
            var nx = x + dx; var ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= width || ny >= height) { continue; }
            var i = ny * width + nx;
            if (_seen[i] == 0 || w.Tick + 1 - _seen[i] >= _rules.RescoutTicks) { value++; }
        }
        return value;
    }

    private long ReconTravel(Cell from, int probe, uint mission) =>
        (_rules.Foundation?.Variety is null ? Fixed.Sqrt(ScoutTravel(from, probe, mission)) : ScoutTravel(from, probe, mission)) / Fixed.OneRaw;

    private long ReconScore(World w, Cell from, int probe, uint mission, AiReconRules p, long danger)
    {
        var age = _seen[probe] == 0 ? p.AgeCapTicks : Math.Min(p.AgeCapTicks, (long)w.Tick + 1 - _seen[probe]);
        var refresh = _intel.Any(c => (long)w.Tick - c.Seen >= p.ContactRefreshTicks &&
            Near(c.Cell, _probes[probe], p.ContactCells)) ? p.ContactBonus : 0;
        return InformationAt(w, probe, p) * (long)p.InformationWeight + age * p.AgeWeight / World.TicksPerSecond + refresh -
            ReconTravel(from, probe, mission) * p.TravelWeight - danger * p.DangerWeight;
    }

    private bool ChooseReconProbe(World w, Mission m)
    {
        var p = _rules.Foundation!.Reconnaissance!;
        var lead = Own(m.Members[0])!;
        var from = Cell.Of(lead.Pos);
        // The cheap available-probe scan still scales with map regions. Bound danger/information and route work separately.
        var probes = Enumerable.Range(0, _probes.Length).Where(i => ProbeAvailable(w, i))
            .OrderBy(i => _seen[i]).ThenBy(i => ReconTravel(from, i, m.Id)).ThenBy(i => i).Take(p.ScoredProbes).ToArray();
        var ranked = new List<(int Probe, long Score)>();
        foreach (var probe in probes)
        {
            var danger = Danger(w, _probes[probe], false);
            if (danger > p.MaxDanger)
            { _retry[probe] = w.Tick + (uint)p.AvoidTicks; continue; }
            ranked.Add((probe, ReconScore(w, from, probe, m.Id, p, danger)));
        }
        foreach (var candidate in ranked.OrderByDescending(c => c.Score).ThenBy(c => c.Probe).Take(p.RouteChecks))
        {
            var probe = candidate.Probe;
            if (!Reachable(w, lead, _probes[probe], out var destination) ||
                !m.Members.All(id => Reachable(w, Own(id)!, destination, out _)))
            { _retry[probe] = w.Tick + (uint)p.AvoidTicks; continue; }
            // Recheck the resolved terrain endpoint; moving off an impassable probe must not bypass its danger gate.
            if (Danger(w, destination, false) > p.MaxDanger)
            { _retry[probe] = w.Tick + (uint)p.AvoidTicks; continue; }
            m.Probe = probe; m.Objective = destination; _probes[probe] = destination;
            Trace(w, m.Id, "scout-information-objective");
            return true;
        }
        Trace(w, m.Id, "scout-no-safe-accessible-probe");
        return false;
    }
}
