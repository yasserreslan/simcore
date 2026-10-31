using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Bots;

/// <summary>Current-frame observation-derived base threat; contacts are copied, never live enemy references.</summary>
public sealed record AiDefenseThreatView(uint Subject, uint Asset, Cell Center, long Strength, long Priority, IReadOnlyList<uint> Contacts);
/// <summary>Retained defense assignment identity and anti-churn deadline.</summary>
public sealed record AiDefenseMemory(uint Threat, uint Asset, uint HoldUntil);

public sealed partial class PlayerAi
{
    private AiDefenseRules? DefensePolicy => _rules.Foundation is { Postures.Count: 5 } foundation ? foundation.DefenseControl : null;

    private sealed record DefenseThreat(uint Subject, uint Asset, Cell Center, long Strength, long Priority, uint[] Contacts);
    private readonly List<DefenseThreat> _defenseThreats = [];
    private readonly List<uint> _defenseAssets = [];
    private Cell _defenseAnchor;
    private long _defenseAirPressure;

    /// <summary>Observed potential air-to-ground pressure near protected assets.</summary>
    public long AirDefensePressure => _defenseAirPressure;

    /// <summary>Base defense anchor from the latest owned observation.</summary>
    public Cell DefenseAnchor => _defenseAnchor;
    /// <summary>Detached current-frame base threats. This diagnostic does not grant new visibility.</summary>
    public IReadOnlyList<AiDefenseThreatView> DefenseThreats => _defenseThreats.Select(t =>
        new AiDefenseThreatView(t.Subject, t.Asset, t.Center, t.Strength, t.Priority, t.Contacts.ToArray())).ToArray();

    // Observe fills this list in world ID order. Resolve only the current visibility snapshot, never World.Unit.
    private Unit? VisibleContact(uint id)
    {
        var lo = 0; var hi = _visible.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2; var unit = _visible[mid];
            if (unit.Id == id) { return unit; }
            if (unit.Id < id) { lo = mid + 1; } else { hi = mid - 1; }
        }
        return null;
    }

    private long AssetValue(Unit u, AiDefenseRules p) => Math.Max(1, (long)u.Def.Cost) +
        (u.Def.Archetype.Production is not null ? p.ProducerValue : 0) +
        (u.Def.Builder is not null ? p.BuilderValue : 0) + (u.Def.Supply is not null ? p.SupplyValue : 0);

    private static bool HasAssetWeapon(Unit enemy) => enemy.Def.Weapons.Any(weapon =>
        (enemy.WeaponMode != WeaponMode.PrimaryOnly || weapon.Index == 0) && (weapon.Effect.Targets & Layers.Ground) != 0 &&
        weapon.Effect.Interceptor is null && weapon.Effect.Cruise is null && World.HasAmmo(enemy, weapon));

    private static bool TargetsAsset(Unit enemy, Unit asset) => enemy.Order.Target == asset.Id || enemy.AutoTarget == asset.Id || enemy.AiTarget == asset.Id;

    private bool NearAssetThreat(Unit enemy, Unit asset, AiDefenseRules p) => Near(Cell.Of(enemy.Pos), Cell.Of(asset.Pos), p.ProtectCells) ||
        TargetsAsset(enemy, asset) && Near(Cell.Of(enemy.Pos), Cell.Of(asset.Pos), p.TravelCells) && enemy.Def.Weapons.Any(weapon =>
            (enemy.WeaponMode != WeaponMode.PrimaryOnly || weapon.Index == 0) && weapon.Effect.Interceptor is null && weapon.Effect.Cruise is null &&
            World.CanHit(weapon, asset) && World.HasAmmo(enemy, weapon) && World.Within(World.From(enemy, asset.Pos), asset.Pos, World.Reach(enemy, weapon)));

    private void ObserveDefense(World w)
    {
        _defenseThreats.Clear(); _defenseAssets.Clear(); _defenseAnchor = Home; _defenseAirPressure = 0;
        if (DefensePolicy is not { } p) { return; }
        var anchor = _own.FirstOrDefault(u => u.IsBuilt && !u.Rubble && u.Def.IsStructure && u.Def.Archetype.Production is not null) ??
            _own.FirstOrDefault(u => u.IsBuilt && !u.Rubble && u.Def.IsStructure && u.Def.Supply is not null);
        _defenseAnchor = anchor is null ? Home : Cell.Of(anchor.Pos);
        var assets = _own.Where(u => u.Inside == 0 && u.IsBuilt && !u.Rubble && (u.Def.IsStructure || u.Def.Builder is not null) &&
                !u.Def.Archetype.Untargetable && Near(Cell.Of(u.Pos), _defenseAnchor, p.CoreCells))
            .OrderByDescending(u => AssetValue(u, p)).ThenBy(u => u.Id).Take(p.Assets).ToArray();
        _defenseAssets.AddRange(assets.Select(u => u.Id));
        foreach (var air in _visible.Where(e => e.Def.Layer == Layers.Air && e.Garrison == 0 && e.Inside == 0 && e.CanAct && HasAssetWeapon(e) &&
                assets.Any(a => NearAssetThreat(e, a, p)))
            .OrderBy(e => Distance(Cell.Of(e.Pos), _defenseAnchor)).ThenBy(e => e.Id).Take(p.Contacts))
        {
            if (assets.Any(a => NearAssetThreat(air, a, p) && w.PickWeapon(air, a, false) >= 0))
            { _defenseAirPressure += Strength(air); }
        }
        var candidates = _visible.Where(e => e.Def.Layer == Layers.Ground && e.Garrison == 0 && e.Inside == 0 && e.CanAct &&
                e.IsBuilt && !e.Rubble && HasAssetWeapon(e) && assets.Any(a => NearAssetThreat(e, a, p)))
            .OrderByDescending(e => assets.Where(a => NearAssetThreat(e, a, p))
                .Select(a => AssetValue(a, p) * Math.Max(1, p.ProtectCells + 1 - Fixed.Sqrt(Distance(Cell.Of(e.Pos), Cell.Of(a.Pos))) / Fixed.OneRaw) +
                    (TargetsAsset(e, a) ? (long)p.TargetedValue * p.ProtectCells : 0))
                .DefaultIfEmpty(0).Max()).ThenBy(e => e.Id).Take(p.Contacts).ToArray();
        var threats = new List<(Unit Enemy, Unit Asset, long Priority)>();
        foreach (var enemy in candidates)
        {
            Unit? asset = null; long priority = long.MinValue;
            foreach (var a in assets)
            {
                if (!NearAssetThreat(enemy, a, p) || w.PickWeapon(enemy, a, false) < 0) { continue; }
                var distance = Fixed.Sqrt(Distance(Cell.Of(enemy.Pos), Cell.Of(a.Pos))) / Fixed.OneRaw;
                var targeted = TargetsAsset(enemy, a);
                var score = AssetValue(a, p) * Math.Max(1, p.ProtectCells + 1 - distance) / p.ProtectCells + Strength(enemy) + (targeted ? p.TargetedValue : 0);
                if (asset is null || score > priority || score == priority && a.Id < asset.Id) { asset = a; priority = score; }
            }
            if (asset is not null) { threats.Add((enemy, asset, priority)); }
        }
        // Fixed seed radius prevents a chain of nearby contacts from bridging two distant raids into one group.
        var groups = new List<List<(Unit Enemy, Unit Asset, long Priority)>>();
        foreach (var threat in threats.OrderByDescending(t => t.Priority).ThenBy(t => t.Enemy.Id))
        {
            var group = groups.Where(g => Near(Cell.Of(g[0].Enemy.Pos), Cell.Of(threat.Enemy.Pos), p.ClusterCells))
                .OrderBy(g => Distance(Cell.Of(g[0].Enemy.Pos), Cell.Of(threat.Enemy.Pos))).ThenBy(g => g[0].Enemy.Id).FirstOrDefault();
            if (group is null)
            { if (groups.Count >= p.Clusters) { continue; } group = []; groups.Add(group); }
            group.Add(threat);
        }
        foreach (var group in groups)
        {
            var at = new Cell((int)(group.Sum(t => (long)Cell.Of(t.Enemy.Pos).X) / group.Count),
                (int)(group.Sum(t => (long)Cell.Of(t.Enemy.Pos).Y) / group.Count));
            var main = group.OrderByDescending(t => t.Priority).ThenBy(t => t.Enemy.Id).First();
            _defenseThreats.Add(new DefenseThreat(main.Enemy.Id, main.Asset.Id, at, group.Sum(t => (long)Strength(t.Enemy)),
                group.Sum(t => t.Priority), group.Select(t => t.Enemy.Id).OrderBy(id => id).ToArray()));
        }
        _defenseThreats.Sort((a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Subject.CompareTo(b.Subject));
    }
}
