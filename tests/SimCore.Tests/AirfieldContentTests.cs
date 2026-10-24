using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;

namespace SimCore.Tests;

// The airfield's content (the game repo's docs/airfield-design.md, sections 1, 1.1 and 1.2): the
// forward airfield type, its five pieces and their flags, the Watcher's aircraft block, the
// weapons' runway effects, and the checks that refuse inconsistent airfield content.
public class AirfieldContentTests
{
    private static readonly ContentSet C = TestContent.Mvp;

    [Fact]
    public void TheForwardAirfieldIsData()
    {
        Assert.Equal(["forward_airbase", "forward_airfield", "main_air_base"], C.AirfieldTypes.Select(a => a.Id)); // H1: the main air base; logistics: the forward airbase (by ID)
        var t = C.AirfieldType("forward_airfield")!;
        Assert.Equal(AircraftClass.M, t.MaxClass);
        Assert.True(t.Serves(AircraftClass.S) && t.Serves(AircraftClass.M) && !t.Serves(AircraftClass.L) && !t.Serves(AircraftClass.XL));
        Assert.Equal((10, 8, 28, 10), (t.StationCells, t.LaneCells, t.RowW, t.RowH));
        Assert.Equal((20, 10, 6, 8, 24), (t.SegmentCells, t.RunwayCells, t.GrassCells, t.TaxiwayCells, t.SegmentDepth));
        Assert.Equal((Fixed.FromInt(10), 3, 30, 1), (t.Connector, t.MinSegments, t.CorridorCells, t.FacilityGapCells));
        Assert.Equal(4 * 512, t.MaxReliefRaw);
        Assert.Equal(["airfield_facility", "airfield_row", "airfield_runway", "airfield_shelter", "airfield_stand"],
            new[] { t.Facility, t.Row, t.Segment, t.Loading, t.Normal }.Select(i => C.Archetypes[i].Id));
        Assert.Equal([(StationKind.Loading, 0, true), (StationKind.Normal, 18, false)], t.RowSlots.Select(s => (s.Kind, s.X, s.Pushback)));
        Assert.All(t.RowSlots, s => Assert.Equal((140, 50), s.ParkIn[0])); // every park-in starts at the lane node
        Assert.Equal((Fixed.FromInt(25), Fixed.FromInt(25), 40 * 512L, 15 * 512L, 4), (t.HoldDistance, t.HoldRadius, t.HoldHeightRaw, t.HoldStepRaw, t.HoldPlaces));
        Assert.Equal((40, 200, 40), (t.LockReleaseTicks, t.TaxiWaitTicks, t.CollapseDamage));
        Assert.Equal((new CraterSpec(Fixed.Frac(3, 2), 300), new CraterSpec(Fixed.FromInt(3), 600)), (t.SmallCrater, t.LargeCrater));
        Assert.Equal((Fixed.FromInt(2), 16, 2, 20, 25, 2), (t.CraterMargin, t.MaxCraters, t.WearPatchCells, t.WearToCrater, t.WearNeighbourPercent, t.WearRepairPerSecond));
    }

    [Fact]
    public void ThePiecesCarryTheirRolesAndFlags()
    {
        var facility = C.Archetype("airfield_facility")!;
        var row = C.Archetype("airfield_row")!;
        var segment = C.Archetype("airfield_runway")!;
        var shelter = C.Archetype("airfield_shelter")!;
        var stand = C.Archetype("airfield_stand")!;
        Assert.Equal([AirfieldRole.Facility, AirfieldRole.Row, AirfieldRole.Segment, AirfieldRole.Station, AirfieldRole.Station],
            new[] { facility, row, segment, shelter, stand }.Select(a => a.Airfield!.Role));
        // Only the facility and the loading shelter are targets and take damage; they fall to rubble.
        foreach (var a in new[] { row, segment, stand })
        {
            Assert.True(a.Untargetable && a.Invulnerable, a.Id);
            Assert.Equal(0, a.RubbleHpPercent);
        }
        Assert.True(row.Pavement && segment.Pavement && stand.Pavement);
        foreach (var a in new[] { facility, shelter })
        {
            Assert.False(a.Untargetable || a.Invulnerable || a.Pavement, a.Id);
            Assert.Equal((50, 50), (a.RubbleHpPercent, a.RepairPercentOfBuild));
        }
        Assert.Equal((10, 10, 2000, 900, 1000), (facility.FootprintW, facility.FootprintH, facility.MaxHp, facility.BuildTime, facility.Cost));
        Assert.Equal(Fixed.FromInt(10), facility.Vision);
        Assert.Equal((28, 10, 400, 300), (row.FootprintW, row.FootprintH, row.BuildTime, row.Cost));
        Assert.Equal((20, 24, 300, 150), (segment.FootprintW, segment.FootprintH, segment.BuildTime, segment.Cost));
        Assert.Equal((1000, 400, 300), (shelter.MaxHp, shelter.BuildTime, shelter.Cost));
        Assert.Equal(C.ArmorClass("building"), facility.Armor);
        Assert.Equal(C.ArmorClass("building"), shelter.Armor);
        // Loading: 2x rearm and repair, 1x refuel, only with the facility; normal: 1x, always.
        var l = shelter.Airfield!;
        var n = stand.Airfield!;
        Assert.Equal((StationKind.Loading, true, 200, 200, 100, Fixed.FromInt(9)), (l.Kind, l.NeedsFacility, l.RearmPercent, l.RepairPercent, l.RefuelPercent, l.MaxSpan));
        Assert.Equal((StationKind.Normal, false, 100, 100, 100, Fixed.Zero), (n.Kind, n.NeedsFacility, n.RearmPercent, n.RepairPercent, n.RefuelPercent, n.MaxSpan));
    }

    [Fact]
    public void OnlyTheSentinelBuildsAirfields()
    {
        var pieces = new[] { "airfield_facility", "airfield_row", "airfield_runway", "airfield_shelter", "airfield_stand" };
        foreach (var id in pieces)
        {
            var i = C.Archetype(id)!.Index;
            Assert.True(C.Faction("sentinel")!.Archetypes[i], id);
            Assert.False(C.Faction("resistance")!.Archetypes[i], id);
        }
    }

    [Fact]
    public void TheWatcherIsAClassSAircraft()
    {
        var a = C.Archetype("watcher_drone")!.Aircraft!;
        Assert.Equal((AircraftClass.S, LandingKind.Runway), (a.SizeClass, a.Landing));
        Assert.Equal((Fixed.Frac(71, 10), Fixed.FromInt(4)), (a.Span, a.Length)); // 14.1 m and 7.9 m after the 0.7 scale
        Assert.Equal((Fixed.FromInt(36), Fixed.FromInt(24)), (a.MinLandRun, a.MinTakeoffRun));
        Assert.Equal((Fixed.FromInt(30), 15 * 512L, Fixed.FromInt(8), Fixed.FromInt(4)), (a.FixDistance, a.FixHeightRaw, a.Touchdown, a.Flare));
        Assert.Equal((Fixed.Frac(50, 200), Fixed.Frac(45, 200), Fixed.Frac(30, 200), Fixed.Frac(15, 200), Fixed.Frac(5, 200), Fixed.Frac(60, 200)),
            (a.ApproachSpeed, a.TouchdownSpeed, a.TaxiSpeed, a.TurnSpeed, a.PushbackSpeed, a.TakeoffSpeed));
        Assert.Equal((400, 300, 10), (a.RefuelTicks, a.RearmTicksPerRound, a.RepairHpPerSecond));
        Assert.Equal((Fixed.FromInt(6), 1280L, 1200, true), (a.GroundVision, a.GroundEyeRaw, a.HoldReserveTicks, a.Sortie));
        // It fits a loading shelter (18 m between the legs) and the forward airfield serves it.
        Assert.True(a.Span <= C.Archetype("airfield_shelter")!.Airfield!.MaxSpan);
        Assert.True(C.AirfieldType("forward_airfield")!.Serves(a.SizeClass));
        // The Watcher, the Overseer (class S too, OverseerTests), the two jets (class M, E8: JetTests) and the ground attack jet (class L,
        // H2: AttackJetTests) carry an aircraft block; its flight block is untouched.
        Assert.Equal(["airlifter", "armored_helicopter", "attack_helicopter", "attack_jet", "fighter_jet", "gunship", "light_helicopter", "multirole_jet", "overseer_drone", "stealth_bomber", "stealth_multirole", "strategic_bomber", "supersonic_bomber", "watcher_drone"],
            C.Archetypes.Where(x => x.Aircraft is not null).Select(x => x.Id)); // (the helicopters land vertically: HelicopterTests)
        Assert.Equal(Fixed.FromInt(25), C.Archetype("watcher_drone")!.Flight!.OrbitRadius);
    }

    // The design's runway table (1.1), guarded: every ground weapon of 250 or more and every
    // ground-attack missile and drone strike craters on impact (the ballistic missile a large
    // one); the light rockets wear; small arms, the mortar, traps and air-only weapons do nothing.
    [Fact]
    public void TheRunwayTableHolds()
    {
        RunwayEffect? Of(string id) => C.Component(id)!.Effects.Select(e => e switch
        {
            WeaponEffect w => w.Runway,
            StrikeEffect s => s.Runway,
            _ => null,
        }).SingleOrDefault(r => r is not null);
        foreach (var id in new[] { "at_missile", "kornet_atgm", "twin_missiles", "watcher_missiles", "drone_rails", "drone_station_rail", "fpv_charge", "heavy_artillery_rockets", "jeep_rockets" })
        {
            Assert.Equal(new RunwayEffect(CraterKind.Small, 0), Of(id));
        }
        Assert.Equal(new RunwayEffect(CraterKind.Large, 0), Of("ballistic_missile"));
        Assert.Equal(new RunwayEffect(CraterKind.Large, 0), C.Archetype("kaman_drone")!.Kaman!.Runway); // the Kaman's dive
        Assert.Equal(new RunwayEffect(CraterKind.Small, 0), Of("kaman_missiles"));
        Assert.Equal((4, 3, 2), (Of("heavy_rockets")!.Wear, Of("medium_rockets")!.Wear, Of("light_rockets")!.Wear));
        foreach (var id in new[] { "rifle", "smg", "lmg", "pistol", "sniper_rifle", "mortar_tube", "heavy_mg", "igla", "aa_missile", "sam_missiles", "ap_mine_charge", "at_mine_charge", "trap_charge" })
        {
            Assert.Null(Of(id));
        }
        foreach (var comp in C.Components)
        {
            foreach (var w in comp.Effects.OfType<WeaponEffect>())
            {
                if ((w.Targets & Layers.Ground) != 0 && w.Damage >= 250 && w.Launches < 0) // a Kaman rail: its drone's blast craters (below)
                {
                    Assert.True(w.Runway is { Crater: not CraterKind.None }, $"{comp.Id}: a ground weapon of {w.Damage} declares no crater");
                }
                if ((w.Targets & Layers.Ground) == 0)
                {
                    Assert.True(w.Runway is null, $"{comp.Id}: an air-only weapon declares a runway effect");
                }
            }
        }
    }

    // Each case edits the MVP content (a file, the text to replace, its replacement; "extra/bad.json"
    // adds a file); the error must say what is wrong and where (the edited file, or the one named).
    public static TheoryData<string, string, string, string[]> BadAirfields => new()
    {
        { "airfields.json", "\"maxClass\": \"M\"", "\"maxClass\": \"Q\"", ["airfield type \"forward_airfield\"", "unknown maxClass \"Q\""] },
        { "airfields.json", "\"lane\": 80", "\"lane\": 85", ["lane is 85, want whole cells"] },
        { "airfields.json", "\"facilityGap\": 10,", "\"facilityGap\": 10, \"hangar\": 1,", ["invalid JSON", "hangar"] },
        { "airfields.json", "\"kind\": \"normal\", \"x\": 180", "\"kind\": \"loading\", \"x\": 180", ["loading and normal stations in equal numbers"] },
        { "airfields.json", "\"x\": 180", "\"x\": 100", ["rowSlots #2", "overlaps the lane"] },
        { "airfields.json", "\"wearPatch\": 20", "\"wearPatch\": 30", ["wearPatch must divide"] },
        { "airfields.json", "\"segment\": \"airfield_runway\"", "\"segment\": \"airfield_strip\"", ["unknown archetype \"airfield_strip\" in pieces.segment"] },
        { "airfields.json", "\"normal\": \"airfield_stand\"", "\"normal\": \"airfield_shelter\"", ["\"airfield_shelter\" (pieces.normal) is already a piece"] },
        { "archetypes/airfield.json", "\"w\": 28, \"h\": 10", "\"w\": 27, \"h\": 10", ["airfields.json", "\"airfield_row\" needs a 28 x 10 footprint, has 27 x 10"] },
        { "archetypes/airfield.json", "\"role\": \"row\"", "\"role\": \"segment\"", ["archetype \"airfield_row\"", "differs from its role"] },
        { "archetypes/airfield.json", "\"airfield\": { \"role\": \"facility\" },", "", ["archetype \"airfield_facility\"", "has no airfield block"] },
        { "archetypes/airfield.json", "\"kind\": \"normal\", \"rearm\": 100", "\"kind\": \"loading\", \"rearm\": 100", ["archetype \"airfield_stand\"", "differs from its kind"] },
        { "archetypes/airfield.json", "\"pavement\": true, \"targetable\": false, \"invulnerable\": true\n    },\n    {\n      // A runway", "\"targetable\": false, \"invulnerable\": true\n    },\n    {\n      // A runway", ["airfields.json", "its row and segment are pavement"] },
        { "archetypes/sentinel.json", "\"sizeClass\": \"S\"", "\"sizeClass\": \"XS\"", ["archetype \"watcher_drone\"", "unknown aircraft.sizeClass \"XS\""] },
        { "archetypes/sentinel.json", "\"touchdown\": 80", "\"touchdown\": 400", ["touchdown must lie within minLandRun"] },
        { "archetypes/sentinel.json", "\"minLandRun\": 360", "\"minLandRun\": 0", ["aircraft.minLandRun is 0"] },
        { "components/vehicle_weapons.json", "\"runway\": { \"wear\": 4 }", "\"runway\": { \"wear\": 4, \"crater\": \"small\" }", ["component \"heavy_rockets\"", "runway: give either a crater or wear"] },
        { "components/vehicle_weapons.json", "\"runway\": { \"crater\": \"large\" }", "\"runway\": { \"crater\": \"huge\" }", ["unknown crater \"huge\""] },
        {
            "extra/bad.json", "",
            """{"archetypes":[{"id":"bad_truck","name":"Bad","glyph":"X","locomotor":"wheeled","armor":"light_vehicle","hp":5,"speed":10,"turnRate":1,"aircraft":{"sizeClass":"S"}}]}""",
            ["archetype \"bad_truck\"", "aircraft is for flying archetypes"]
        },
        {
            "extra/bad.json", "",
            """{"archetypes":[{"id":"bad_pad","name":"Bad","glyph":"X","locomotor":"none","armor":"building","hp":5,"footprint":{"w":2,"h":2},"buildTime":1,"airfield":{"role":"row"}}]}""",
            ["archetype \"bad_pad\"", "no airfield type names it"]
        },
        {
            "extra/bad.json", "",
            """{"archetypes":[{"id":"bad_soldier","name":"Bad","glyph":"X","locomotor":"foot","armor":"infantry","hp":5,"speed":10,"turnRate":1,"pavement":true}]}""",
            ["archetype \"bad_soldier\"", "pavement, rubble and repairable need a footprint"]
        },
    };

    [Theory]
    [MemberData(nameof(BadAirfields))]
    public void RejectsBadAirfieldContent(string file, string find, string replace, string[] want)
    {
        var sources = TestContent.MvpSources();
        if (file.StartsWith("extra/", StringComparison.Ordinal))
        {
            sources.Add(new ContentSource(file, replace));
        }
        else
        {
            var i = sources.FindIndex(s => s.Name == file);
            var json = sources[i].Json.Replace("\r\n", "\n");
            Assert.Contains(find, json, StringComparison.Ordinal);
            sources[i] = sources[i] with { Json = ReplaceFirst(json, find, replace) };
        }
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.Contains(want[0].EndsWith(".json", StringComparison.Ordinal) ? want[0] : file, e.Message, StringComparison.Ordinal);
        foreach (var w in want)
        {
            Assert.True(e.Message.Contains(w, StringComparison.Ordinal), $"\"{e.Message}\" lacks \"{w}\"");
        }
    }

    private static string ReplaceFirst(string s, string find, string replace)
    {
        var i = s.IndexOf(find, StringComparison.Ordinal);
        return s[..i] + replace + s[(i + find.Length)..];
    }

    [Fact]
    public void AirfieldValuesAreInTheContentHash()
    {
        foreach (var (file, find, replace) in new[]
        {
            ("airfields.json", "\"collapseDamage\": 40", "\"collapseDamage\": 41"),
            ("archetypes/sentinel.json", "\"span\": 71", "\"span\": 72"),
            ("archetypes/airfield.json", "\"rearm\": 200", "\"rearm\": 199"),
            ("components/vehicle_weapons.json", "\"runway\": { \"wear\": 4 }", "\"runway\": { \"wear\": 5 }"),
        })
        {
            var sources = TestContent.MvpSources().Select(s => s.Name == file ? s with { Json = ReplaceFirst(s.Json, find, replace) } : s).ToList();
            Assert.NotEqual(C.Hash, ContentLoader.Load(sources).Hash);
        }
    }
}
