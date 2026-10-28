using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Content;

// The infantry squad block (docs/design.md, "Infantry squads").
internal static partial class ContentCompiler
{
    private static readonly string[] SquadRoleNames = ["line", "medic", "recon", "flank", "longFlank", "airGuard", "support"];

    // CompileSquad reads the one optional squad block (two are an error); every field is required.
    private static SquadRules? CompileSquad(IReadOnlyList<(string File, ContentFile Data)> files, Context c)
    {
        string? at = null;
        SquadJson? j = null;
        foreach (var (file, data) in files)
        {
            if (data.Squad is null)
            {
                continue;
            }
            if (at is not null)
            {
                throw Fail(file, $"squad: already defined in {at}");
            }
            (at, j) = (file, data.Squad);
        }
        if (j is null)
        {
            return null;
        }
        const string where = "squad";
        var f = at!;
        T Need<T>(string field, T? v) where T : class => v ?? throw Fail(f, $"{where}: {field} is required");
        int NeedInt(string field, int? v, int min, int max) => v is { } x ? Num(f, where, field, x, min, max) : throw Fail(f, $"{where}: {field} is required");
        SquadRole Role(string field, string? name)
        {
            var i = Array.IndexOf(SquadRoleNames, name);
            return i >= 0 ? (SquadRole)i : throw Fail(f, $"{where}: {field}: unknown role \"{name}\" (want {string.Join(", ", SquadRoleNames)})");
        }
        var size = Need("size", j.Size);
        if (size.Count != 2)
        {
            throw Fail(f, $"{where}: size: want [fewest, most]");
        }
        var min = Num(f, where, "size[0]", size[0], 2, 64);
        var max = Num(f, where, "size[1]", size[1], min, 64);
        var roles = new List<(TagSet, SquadRole)>();
        foreach (var r in Need("roles", j.Roles))
        {
            roles.Add((Tags(c, f, where, "roles.tag", [Need("roles.tag", r.Tag)]), Role("roles.role", r.Role)));
        }
        var noLead = 0;
        foreach (var name in Need("noLead", j.NoLead))
        {
            noLead |= 1 << (int)Role("noLead", name);
        }
        var march = Need("march", j.March);
        var assault = Need("assault", j.Assault);
        SquadPlace Place(string field, List<int>? v)
        {
            if (v is not { Count: 2 })
            {
                throw Fail(f, $"{where}: assault.{field}: want [back, gap] in tenths of a cell");
            }
            return new SquadPlace(Tenths(Num(f, where, $"assault.{field}[0]", v[0], 0, 500)), Tenths(Num(f, where, $"assault.{field}[1]", v[1], 0, 500)));
        }
        var spacing = Tenths(NeedInt("assault.spacing", assault.Spacing, 5, 100));
        var places = new SquadPlace[SquadRoleNames.Length];
        places[(int)SquadRole.Line] = new SquadPlace(Fixed.Zero, spacing);
        places[(int)SquadRole.Flank] = new SquadPlace(Fixed.Zero, spacing);
        places[(int)SquadRole.Medic] = Place("medic", assault.Medic);
        places[(int)SquadRole.Recon] = Place("recon", assault.Recon);
        places[(int)SquadRole.LongFlank] = Place("longFlank", assault.LongFlank);
        places[(int)SquadRole.AirGuard] = Place("airGuard", assault.AirGuard);
        places[(int)SquadRole.Support] = Place("support", assault.Support);
        return new SquadRules
        {
            MinSize = min,
            MaxSize = max,
            Need = Tags(c, f, where, "need", Need("need", j.Need)),
            Never = Tags(c, f, where, "never", Need("never", j.Never)),
            Roles = roles,
            NoLeadMask = noLead,
            MarchSpacing = Tenths(NeedInt("march.spacing", march.Spacing, 5, 100)),
            MarchSpeedPercent = NeedInt("march.speed", march.Speed, 10, 100),
            MarchAhead = Tenths(NeedInt("march.ahead", march.Ahead, 0, 200)),
            MarchAirGuardGap = Tenths(NeedInt("march.airGuardGap", march.AirGuardGap, 5, 200)),
            MarchSupportGap = Tenths(NeedInt("march.supportGap", march.SupportGap, 5, 200)),
            AssaultSpacing = spacing,
            AssaultSpeedPercent = NeedInt("assault.speed", assault.Speed, 10, 100),
            AssaultPlaces = places,
            AssaultReconSide = Tenths(NeedInt("assault.reconSide", assault.ReconSide, 0, 200)),
            Regroup = Tenths(NeedInt("regroup", j.Regroup, 10, 500)),
            RegroupTicks = NeedInt("regroupTicks", j.RegroupTicks, 0, Limit),
            GroupGap = Tenths(NeedInt("groupGap", j.GroupGap, 10, 1000)),
            Hold = j.Hold is { } hold ? CompileHold(f, hold) : null,
        };
    }

    // CompileHold reads the squad block's optional hold part: every field is required; the pick order names each role once.
    private static SquadHoldRules CompileHold(string f, SquadHoldJson j)
    {
        const string where = "squad.hold";
        T Need<T>(string field, T? v) where T : class => v ?? throw Fail(f, $"{where}: {field} is required");
        int NeedInt(string field, int? v, int min, int max) => v is { } x ? Num(f, where, field, x, min, max) : throw Fail(f, $"{where}: {field} is required");
        SquadRole Role(string field, string? name)
        {
            var i = Array.IndexOf(SquadRoleNames, name);
            return i >= 0 ? (SquadRole)i : throw Fail(f, $"{where}: {field}: unknown role \"{name}\" (want {string.Join(", ", SquadRoleNames)})");
        }
        var range = Need("threatRange", j.ThreatRange);
        if (range.Count != 2)
        {
            throw Fail(f, $"{where}: threatRange: want [nearest, farthest] in tenths of a cell");
        }
        var tmin = NeedInt("threatRange[0]", range[0], 10, 1000);
        var tmax = NeedInt("threatRange[1]", range[1], tmin, 1000);
        var cover = Need("cover", j.Cover);
        var order = new List<SquadRole>();
        foreach (var name in Need("order", j.Order))
        {
            var r = Role("order", name);
            if (order.Contains(r))
            {
                throw Fail(f, $"{where}: order: \"{name}\" twice");
            }
            order.Add(r);
        }
        if (order.Count != SquadRoleNames.Length)
        {
            throw Fail(f, $"{where}: order: want every role once ({string.Join(", ", SquadRoleNames)})");
        }
        var roles = Need("roles", j.Roles);
        SquadHoldRole HoldRole(string name, SquadHoldRoleJson? r)
        {
            var at = $"roles.{name}";
            r = Need(at, r);
            var w = Need($"{at}.weights", r.Weights);
            if (w.Count != 8)
            {
                throw Fail(f, $"{where}: {at}.weights: want [cover, trees, sight, open, spacing, safety, range, near]");
            }
            int W(int i) => Num(f, where, $"{at}.weights[{i}]", w[i], 0, 100);
            var apart = Need($"{at}.apart", r.Apart);
            if (apart.Count != 2)
            {
                throw Fail(f, $"{where}: {at}.apart: want [from anyone, from the same role] in tenths of a cell");
            }
            return new SquadHoldRole(
                new SquadHoldWeights(W(0), W(1), W(2), W(3), W(4), W(5), W(6), W(7)),
                Tenths(NeedInt($"{at}.back", r.Back, -1000, 1000)),
                Tenths(Num(f, where, $"{at}.apart[0]", apart[0], 0, 500)),
                Tenths(Num(f, where, $"{at}.apart[1]", apart[1], 0, 500)),
                Tenths(NeedInt($"{at}.spread", r.Spread, 1, 500)),
                r.Garrison ?? throw Fail(f, $"{where}: {at}.garrison is required"));
        }
        return new SquadHoldRules
        {
            Radius = Tenths(NeedInt("radius", j.Radius, 10, 400)),
            Cells = NeedInt("cells", j.Cells, 1, 10_000),
            Threat = Tenths(NeedInt("threat", j.Threat, 10, 1000)),
            ThreatMin = Tenths(tmin),
            ThreatMax = Tenths(tmax),
            LaneMax = Tenths(NeedInt("laneMax", j.LaneMax, 10, 640)), // a lane is at most 64 steps
            LaneSpread = Tenths(NeedInt("laneSpread", j.LaneSpread, 0, 500)),
            CoverWall = NeedInt("cover.wall", cover.Wall, 0, 100),
            CoverTrunk = NeedInt("cover.trunk", cover.Trunk, 0, 100),
            CoverGrove = NeedInt("cover.grove", cover.Grove, 0, 100),
            CoverDense = NeedInt("cover.dense", cover.Dense, 0, 100),
            CoverCrest = NeedInt("cover.crest", cover.Crest, 0, 100),
            Order = order,
            Roles =
            [
                HoldRole("line", roles.Line), HoldRole("medic", roles.Medic), HoldRole("recon", roles.Recon), HoldRole("flank", roles.Flank),
                HoldRole("longFlank", roles.LongFlank), HoldRole("airGuard", roles.AirGuard), HoldRole("support", roles.Support),
            ],
        };
    }
}
