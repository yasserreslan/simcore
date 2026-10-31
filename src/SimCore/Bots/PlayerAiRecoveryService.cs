using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

public sealed partial class PlayerAi
{
    private AiRecoveryRules? RecoveryPolicy => _rules.Foundation?.CombatControl?.Recovery;

    // Weapon pools only: ability kits, fuel, cargo and garage/airfield service belong to their executors.
    // Match SupplySystem's special reload restrictions before valuing an aura supplier.
    private long RecoveryAmmoDemand(Unit provider, Unit unit, out bool useful)
    {
        useful = false;
        if (provider.Id == unit.Id || provider.Inside != 0 || !provider.IsBuilt || provider.Rubble ||
            provider.Def.Supply is not { Storage: false } supply || supply.GroundOnly &&
            (unit.Def.Layer == Layers.Air || unit.Def.Archetype.Flight is not null || unit.Def.Archetype.Aircraft is not null)) { return 0; }
        var pools = new List<int>(); long demand = 0;
        foreach (var weapon in unit.Def.Weapons)
        {
            if (weapon.Stock < 0 || unit.WeaponMode == WeaponMode.PrimaryOnly && weapon.Index != 0 || pools.Contains(weapon.Stock)) { continue; }
            pools.Add(weapon.Stock);
            var wanted = ((long)unit.StockMax(weapon.Stock) * _rules.Foundation!.CombatControl!.RecoveryAmmoPercent + 99) / 100;
            var missing = Math.Max(0, wanted - unit.Charges[weapon.Stock]);
            if (missing == 0) { continue; }
            var special = unit.Def.Weapons.FirstOrDefault(w => w.Stock == weapon.Stock && w.Effect.DepotReload is not null)?.Effect.DepotReload;
            if (special is not null && !provider.Def.IsStructure || special is null && (supply.Amount <= 0 ||
                unit.Def.Weapons.Any(w => w.Stock == weapon.Stock && (w.Effect.NoResupply || w.Effect.TruckReload is not null)))) { continue; }
            var cost = special?.Cost ?? supply.RoundCost;
            demand += missing * cost;
            useful |= supply.Stock == 0 || provider.Stock >= cost;
        }
        return demand;
    }

    private bool RecoveryRepairs(Unit provider, Unit member, Cell point) => provider.Id != member.Id &&
        provider.Inside == 0 && provider.CanAct && provider.IsBuilt && !provider.Rubble &&
        (long)member.HP * 100 < (long)member.Def.MaxHp * RecoveryPolicy!.ReadyHpPercent &&
        provider.Def.Repairs.Any(r => member.Def.Tags.Matches(r.Affects) && World.Within(point.Center(), provider.Pos, r.Radius));

    private bool RecoverySupplies(Unit provider, Unit member, Cell point)
    {
        RecoveryAmmoDemand(provider, member, out var useful);
        return useful && World.Within(point.Center(), provider.Pos, provider.Def.Supply!.Radius);
    }

    private bool RecoveryProviderUseful(Unit provider, IReadOnlyList<Unit> members, Cell point) =>
        members.Any(u => RecoverySupplies(provider, u, point) || RecoveryRepairs(provider, u, point));

    private long RecoveryServiceCost(Unit provider, IReadOnlyList<Unit> members, Cell point, uint mission, bool claims)
    {
        var p = _rules.Foundation!.CombatControl!;
        var supply = members.Count(u => RecoverySupplies(provider, u, point));
        var repair = members.Count(u => RecoveryRepairs(provider, u, point));
        long cost = -(long)p.SupplyBonus * supply / Math.Max(1, members.Count) - (long)p.RepairBonus * repair / Math.Max(1, members.Count);
        if (!claims || supply + repair == 0) { return cost; }
        var reserved = new List<uint>();
        // Existing ownership is the reservation ledger. Rebuild it on demand; do not retain live unit references.
        foreach (var force in _missions)
        {
            if (force.Done || force.Id == mission || force.Phase != AiMissionPhase.Returning || force.Combat?.RecoveryTarget != provider.Id) { continue; }
            foreach (var id in force.Members)
            { var at = reserved.BinarySearch(id); if (at < 0) { reserved.Insert(~at, id); } }
        }
        long demand = members.Sum(u => RecoveryAmmoDemand(provider, u, out _));
        var queued = 0;
        foreach (var other in _own)
        {
            if (other.Inside != 0 || !other.IsBuilt || other.Rubble || members.Any(u => u.Id == other.Id) ||
                reserved.BinarySearch(other.Id) < 0 && !(provider.Def.Supply is { } radius && World.Within(other.Pos, provider.Pos, radius.Radius)) &&
                !provider.Def.Repairs.Any(r => World.Within(other.Pos, provider.Pos, r.Radius))) { continue; }
            var ammo = RecoveryAmmoDemand(provider, other, out var useful);
            if (!useful && !RecoveryRepairs(provider, other, point)) { continue; }
            demand += ammo; queued++;
        }
        cost += (long)queued * RecoveryPolicy!.QueuePenalty;
        if (provider.Def.Supply is { Stock: > 0 } && demand > provider.Stock)
        { cost += RecoveryPolicy.StockPenalty - (long)provider.Stock * RecoveryPolicy.StockPenalty / demand; }
        return cost;
    }

    private bool RecoveryCareInRange(Mission m)
    {
        if (m.Combat is not { RecoveryTarget: > 0 } memory || Own(memory.RecoveryTarget) is not { } provider) { return true; }
        foreach (var id in m.Members)
        {
            var member = Own(id)!;
            if (RecoverySupplies(provider, member, m.Objective) && !World.Within(member.Pos, provider.Pos, provider.Def.Supply!.Radius) ||
                RecoveryRepairs(provider, member, m.Objective) && !provider.Def.Repairs.Any(r =>
                    member.Def.Tags.Matches(r.Affects) && World.Within(member.Pos, provider.Pos, r.Radius))) { return false; }
        }
        return true;
    }

    // Arrival is not completion when this destination can still perform useful work. The original absolute
    // recovery deadline is authoritative; an incompatible/unavailable provider never creates an endless wait.
    private bool WaitingForRecovery(Mission m) => RecoveryPolicy is not null && m.Combat is { RecoveryTarget: > 0 } memory &&
        Own(memory.RecoveryTarget) is { } provider && RecoveryProviderUseful(provider, m.Members.Select(id => Own(id)!).ToArray(), m.Objective);
}
