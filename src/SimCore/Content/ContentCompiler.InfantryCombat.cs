using SimCore.Model;

namespace SimCore.Content;

// The infantryCombat block (INF-E1b-1; docs/design.md, "Infantry combat: the ground and the senses").
internal static partial class ContentCompiler
{
    // CompileInfantryCombat reads the one optional infantryCombat block (two are an error); every number is required, unknown keys
    // are errors; each listed weapon must be a component with a weapon.
    private static InfantryCombatRules? CompileInfantryCombat(IReadOnlyList<(string File, ContentFile Data)> files, Context c,
        IReadOnlyList<Component> components)
    {
        string? at = null;
        InfantryCombatJson? j = null;
        foreach (var (file, data) in files)
        {
            if (data.InfantryCombat is null)
            {
                continue;
            }
            if (at is not null)
            {
                throw Fail(file, $"infantryCombat: already defined in {at}");
            }
            (at, j) = (file, data.InfantryCombat);
        }
        if (j is null)
        {
            return null;
        }
        const string where = "infantryCombat";
        var f = at!;
        var values = new int[CombatNames.Names.Length];
        var seen = new bool[values.Length];
        var groups = new[] { j.Perception, j.Danger, j.Exposure, j.Cover, j.Posture, j.Suppression, j.Zones, j.Think, j.Gaits, j.Facing, j.Position, j.Targeting, j.Grenades, j.VehicleCover };
        for (var gi = 0; gi < groups.Length; gi++)
        {
            var name = CombatNames.Groups[gi];
            var g = groups[gi] ?? throw Fail(f, $"{where}: {name} is required");
            foreach (var (key, v) in g.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var i = Array.IndexOf(CombatNames.Names, $"{name}.{key}");
                if (i < 0)
                {
                    throw Fail(f, $"{where}: {name}: unknown field \"{key}\"");
                }
                values[i] = Num(f, where, $"{name}.{key}", v, 0, Limit);
                seen[i] = true;
            }
        }
        for (var i = 0; i < seen.Length; i++)
        {
            if (!seen[i])
            {
                throw Fail(f, $"{where}: {CombatNames.Names[i]} is required");
            }
        }
        int V(CombatValue v) => values[(int)v];
        if (V(CombatValue.ReactMin) > V(CombatValue.ReactMax) || V(CombatValue.SurpriseMin) > V(CombatValue.SurpriseMax) ||
            V(CombatValue.ContactsPerSoldier) is < 1 or > 16 || V(CombatValue.ContactsPerSquad) is < 1 or > 32 ||
            V(CombatValue.Shaken) > V(CombatValue.Pinned) || V(CombatValue.Pinned) > V(CombatValue.PinnedHard) || V(CombatValue.PinnedHard) > 100 ||
            V(CombatValue.SoldierEvery) < 1 || V(CombatValue.SoldierCalmEvery) < 1 || V(CombatValue.SquadEvery) < 1 || V(CombatValue.AreaTicks) < 1)
        {
            throw Fail(f, $"{where}: react and surprise min at most max; 1 to 16 contacts a soldier, 1 to 32 a squad; shaken, pinned and pinnedHard rising to 100 at most; cadences above 0");
        }
        var weapons = new List<CombatWeapon>();
        foreach (var (id, w) in (j.Weapons ?? throw Fail(f, $"{where}: weapons is required")).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var comp = components.FirstOrDefault(x => x.Id == id) ?? throw Fail(f, $"{where}: weapons: unknown component \"{id}\"");
            var effect = comp.Effects.OfType<WeaponEffect>().FirstOrDefault() ?? throw Fail(f, $"{where}: weapons: \"{id}\" has no weapon");
            var cls = w.Class switch
            {
                "rifle" => CombatClass.Rifle,
                "carbine" => CombatClass.Carbine,
                "smg" => CombatClass.Smg,
                "pistol" => CombatClass.Pistol,
                "lmg" => CombatClass.Lmg,
                "marksman" => CombatClass.Marksman,
                "launcher" => CombatClass.Launcher,
                "crew" => CombatClass.Crew,
                _ => throw Fail(f, $"{where}: weapons: \"{id}\": class must be rifle, carbine, smg, pistol, lmg, marksman, launcher or crew"),
            };
            weapons.Add(new CombatWeapon(id, effect, cls, Num(f, where, $"weapons.{id}.sound", w.Sound ?? -1, 0, Limit),
                Num(f, where, $"weapons.{id}.suppress", w.Suppress ?? -1, 0, 100)));
        }
        return new InfantryCombatRules
        {
            Values = values,
            Weapons = weapons,
            ArmourTags = Tags(c, f, where, "armourTags", j.ArmourTags ?? throw Fail(f, $"{where}: armourTags is required")),
            LightVehicleTags = Tags(c, f, where, "lightVehicleTags", j.LightVehicleTags ?? throw Fail(f, $"{where}: lightVehicleTags is required")),
            HelicopterTags = Tags(c, f, where, "helicopterTags", j.HelicopterTags ?? throw Fail(f, $"{where}: helicopterTags is required")),
            CoverHighTags = Tags(c, f, where, "coverHighTags", j.CoverHighTags ?? throw Fail(f, $"{where}: coverHighTags is required")),
            CoverLowTags = Tags(c, f, where, "coverLowTags", j.CoverLowTags ?? throw Fail(f, $"{where}: coverLowTags is required")),
            GrenadeCarry = [.. (j.GrenadeCarry ?? throw Fail(f, $"{where}: grenadeCarry is required")).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, Num(f, where, $"grenadeCarry.{kv.Key}", kv.Value, 0, 9)))],
        };
    }
}
