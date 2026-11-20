using SimCore.Model;

namespace SimCore.Content;

// The soldiers block (INF-E2a; docs/design.md, "Wounds, rescue, bodies, ranks and Retreat").
internal static partial class ContentCompiler
{
    // The numbers that may be negative (percent changes); every other one is 0 or more.
    private static readonly SoldierValue[] SoldierSigned = [SoldierValue.WoundedSpeed, SoldierValue.WoundedAim, SoldierValue.WoundedPosture];

    // CompileSoldiers reads the one optional soldiers block (two are an error); every field is required, unknown keys are errors.
    private static SoldierRules? CompileSoldiers(IReadOnlyList<(string File, ContentFile Data)> files, Context c)
    {
        string? at = null;
        SoldiersJson? j = null;
        foreach (var (file, data) in files)
        {
            if (data.Soldiers is null)
            {
                continue;
            }
            if (at is not null)
            {
                throw Fail(file, $"soldiers: already defined in {at}");
            }
            (at, j) = (file, data.Soldiers);
        }
        if (j is null)
        {
            return null;
        }
        const string where = "soldiers";
        var f = at!;
        var values = new int[SoldierRules.Names.Length];
        var seen = new bool[values.Length];
        void Group(string name, Dictionary<string, int>? g)
        {
            if (g is null)
            {
                throw Fail(f, $"{where}: {name} is required");
            }
            foreach (var (key, v) in g.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var i = Array.IndexOf(SoldierRules.Names, $"{name}.{key}");
                if (i < 0)
                {
                    throw Fail(f, $"{where}: {name}: unknown field \"{key}\"");
                }
                var signed = Array.IndexOf(SoldierSigned, (SoldierValue)i) >= 0;
                values[i] = Num(f, where, $"{name}.{key}", v, signed ? -100 : 0, signed ? 1000 : Limit);
                seen[i] = true;
            }
        }
        Group("wounds", j.Wounds);
        Group("rescue", j.Rescue);
        Group("medic", j.Medic);
        Group("bodies", j.Bodies);
        Group("scavenge", j.Scavenge);
        Group("ranks", j.Ranks);
        Group("retreat", j.Retreat);
        for (var i = 0; i < seen.Length; i++)
        {
            if (!seen[i])
            {
                throw Fail(f, $"{where}: {SoldierRules.Names[i]} is required");
            }
        }
        int V(SoldierValue v) => values[(int)v];
        if (V(SoldierValue.DownPercent) >= V(SoldierValue.WoundedPercent) || V(SoldierValue.WoundedPercent) > 100)
        {
            throw Fail(f, $"{where}: wounds.down must be under wounds.wounded, and wounds.wounded at most 100");
        }
        if (V(SoldierValue.RankRegular) > V(SoldierValue.RankVeteran) || V(SoldierValue.RankVeteran) > V(SoldierValue.RankElite))
        {
            throw Fail(f, $"{where}: ranks.regular, ranks.veteran and ranks.elite must rise");
        }
        if (V(SoldierValue.RetreatMin) > V(SoldierValue.RetreatMax) || V(SoldierValue.BoundMin) > V(SoldierValue.BoundMax) ||
            V(SoldierValue.BoundMax) <= 0 || V(SoldierValue.BodyMax) <= 0)
        {
            throw Fail(f, $"{where}: retreat.min/max and boundMin/boundMax must rise (boundMax above 0), bodies.max above 0");
        }
        var xp = new List<(TagSet, int)>();
        foreach (var e in j.VehicleXp ?? throw Fail(f, $"{where}: vehicleXp is required"))
        {
            xp.Add((Tags(c, f, where, "vehicleXp.tag", [e.Tag ?? throw Fail(f, $"{where}: vehicleXp.tag is required")]),
                Num(f, where, "vehicleXp.points", e.Points ?? throw Fail(f, $"{where}: vehicleXp.points is required"), 0, Limit)));
        }
        return new SoldierRules
        {
            Need = Tags(c, f, where, "need", j.Need ?? throw Fail(f, $"{where}: need is required")),
            Medics = Tags(c, f, where, "medics", j.Medics ?? throw Fail(f, $"{where}: medics is required")),
            NoScavenge = Tags(c, f, where, "noScavenge", j.NoScavenge ?? throw Fail(f, $"{where}: noScavenge is required")),
            VehicleXp = xp,
            Values = values,
        };
    }
}
