using System.Text.Json;
using System.Text.Json.Nodes;
using SimCore.Content;
using SimCore.Model;

namespace SimCore.Tests;

public class ContentTests
{
    [Fact]
    public void MvpLoads()
    {
        var c = TestContent.Mvp;
        Assert.Equal(92, c.Archetypes.Count); // the light tank; the main battle tank; the Humvee; logistics: the airlifter, the field ammo point, the supply stash, the forward airbase and its garage, apron and stand; the stealth bomber; the SA-6 launcher; the mobile radar; the Kaman drone, its truck and the engineer's Kaman station; the supersonic bomber; the three helicopters and the helipad; the assault medic; the military garage and the Sentinel cargo truck; the high-altitude interceptor launcher; the missile defence launcher, the radar truck and the reload pad; the strategic bomber (H4); the gunship (H3); the ground attack jet (H2); the main air base's core, command building, hangar, heavy bay, radar and runway segment and the radar site (H1); the two jets and the pilot (E8); the Sentinel's Overseer control station; the Sentinel's Overseer drone; the Sentinel bulldozer; the forward airfield's five pieces; the Sentinel's Watcher drone and its ground control station; manpads (air defence), the vehicle garage, the infantry carrier, the air defence vehicle, the scout drone, its truck and station, the observation post, the two mines, the mortar, the three tunnel entrances
        Assert.Equal(88, c.Components.Count); // the light tank's gun; the tank's gun, machine guns and Trophy; the Humvee's MG and smoke launchers; logistics: the cargo pallets and the four stocks; the stealth bomber's precision bombs and bunker busters; the SA-6 launcher's missiles; the Kaman truck's rail, the Kaman station's rail, the Kaman's missiles and the Kaman station kit; the supersonic bomber's bombs; the helicopters' seven weapons; the medic kit; the high-altitude interceptors; the missile defence launcher's interceptors; the strategic bomber's bombs (H4); the gunship's three side cannons (H3); the ground attack jet's bombs, gun and light IR missiles (H2); the jets' two missiles, two guns, three bomb loads and the pilot's compact rifle (E8); the Overseer's launch; the bulldozer's builder; the Watcher's missiles and its station's launch; with sam_missiles, the three scout launchers and kit, the two mine kits and charges, the mortar tube
        Assert.Equal(["resistance", "sentinel"], c.Factions.Select(f => f.Id));
        Assert.Equal(["aa_missile", "air_gun", "air_strike", "ap_blast", "apfsds", "at_blast", "atgm", "autocannon", "ballistic", "bomb", "bullet", "bunker_buster", "crash", "drone_charge", "explosive", "he_mpat", "heavy_bullet", "heavy_cannon", "heavy_missile", "heavy_rocket", "heli_cannon", "heli_cannon_he", "heli_missile", "kaman_charge", "mortar_bomb", "mrl_rocket", "rocket", "sniper_round"], c.Warheads.Select(w => w.Id));
        Assert.Equal(["aircraft", "armored_vehicle", "building", "drone", "infantry", "light_vehicle", "medium_vehicle", "parked_aircraft"], c.ArmorClasses);
        Assert.Matches("^[0-9a-f]{64}$", c.Hash);

        Assert.Equal(Layers.Air, c.Archetype("fpv_drone")!.Layer);
        Assert.Equal(Layers.Ground, c.Archetype("technical")!.Layer);
        Assert.True(c.Archetype("booby_trap")!.Hidden);
        Assert.True(c.Component("pistol")!.Placeholder);
        Assert.False(c.Component("rifle")!.Placeholder);
        Assert.Equal((6, 6), (c.Archetype("supply_depot")!.FootprintW, c.Archetype("supply_depot")!.FootprintH));

        var assault = c.Archetype("assault")!;
        Assert.Equal(["rifle", "smg", "lmg"], assault.SlotChoices[(int)SlotKind.Primary].Select(i => c.Components[i].Id));
        Assert.Equal(["pistol"], assault.SlotChoices[(int)SlotKind.Secondary].Select(i => c.Components[i].Id));
        Assert.False(c.Archetype("worker")!.HasSlots);
        Assert.True(c.Archetype("sniper")!.Vision > c.Archetype("assault")!.Vision);
        Assert.True(c.Archetype("sniper")!.Detect.Raw > 0);
        // Detectors (traps): the sniper, the scout drone and the observation post only (2026-10-18).
        Assert.Equal(["sniper", "scout_drone", "observation_post"],
            c.Archetypes.Where(a => a.Detect.Raw > 0).Select(a => a.Id).OrderBy(id => id == "sniper" ? 0 : id == "scout_drone" ? 1 : 2));

        foreach (var id in new[] { "dirt_bike", "technical", "light_rocket_truck", "medium_rocket_launcher", "heavy_rocket_launcher", "heavy_rocket_truck", "missile_truck", "cargo_truck", "infantry_carrier" })
        {
            Assert.NotNull(c.Archetype(id)!.Crew);
        }
        var resistance = c.Faction("resistance")!;
        Assert.Equal(3, resistance.MaxPresetsPerArchetype);
        Assert.True(resistance.Components[c.Component("drone_kit")!.Index]);
        Assert.False(resistance.Components[c.Component("heavy_mg")!.Index]); // vehicle parts are fixed, not picked

        // Launcher ranges grow light → medium → rocket truck: 6, 12 and 60 cells.
        int Range(string id) => c.Archetype(id)!.Components.SelectMany(i => c.Components[i].Effects).OfType<WeaponEffect>().Single().Range.Floor();
        Assert.Equal((6, 12, 60), (Range("light_rocket_truck"), Range("medium_rocket_launcher"), Range("heavy_rocket_launcher")));
    }

    // One drone type per station (2026-10-19): every station (a structure or placed station that
    // launches drones) carries exactly one launch ability, and each relaunching drone (the Watcher,
    // the Overseer) has exactly one station archetype of its own. The Watcher and the Overseer
    // control stations share the footprint, HP, cost and build time (night defaults).
    [Fact]
    public void EveryStationLaunchesOneDroneType()
    {
        var c = TestContent.Mvp;
        IEnumerable<LaunchEffect> Launches(Archetype a) => a.Components.SelectMany(i => c.Components[i].Effects).OfType<LaunchEffect>();
        var stations = c.Archetypes.Where(a => a.Locomotor == Locomotor.None && Launches(a).Any()).ToList();
        Assert.Contains(stations, a => a.Id == "watcher_station");
        Assert.Contains(stations, a => a.Id == "overseer_station");
        foreach (var a in stations)
        {
            Assert.True(Launches(a).Count() == 1, $"{a.Id} launches {Launches(a).Count()} drone types");
        }
        foreach (var drone in c.Archetypes.Where(a => a.Flight is { Relaunch: true }))
        {
            var homes = stations.Where(a => Launches(a).Single().Archetype == drone.Index).Select(a => a.Id).ToList();
            Assert.True(homes.Count == 1, $"{drone.Id}: stations {string.Join(", ", homes)}");
        }
        Assert.Equal(c.Archetype("watcher_drone")!.Index, Launches(c.Archetype("watcher_station")!).Single().Archetype);
        Assert.Equal(c.Archetype("overseer_drone")!.Index, Launches(c.Archetype("overseer_station")!).Single().Archetype);

        var ws = c.Archetype("watcher_station")!;
        var os = c.Archetype("overseer_station")!;
        Assert.Equal(("Watcher control station", "Overseer control station"), (ws.Name, os.Name));
        Assert.Equal((ws.FootprintW, ws.FootprintH, ws.MaxHp, ws.Cost, ws.BuildTime), (os.FootprintW, os.FootprintH, os.MaxHp, os.Cost, os.BuildTime));
        Assert.Equal((12, 8, 1200, 1500, 900), (os.FootprintW, os.FootprintH, os.MaxHp, os.Cost, os.BuildTime));
        var sentinel = c.Faction("sentinel")!;
        Assert.True(sentinel.Archetypes[ws.Index] && sentinel.Archetypes[os.Index]);
        Assert.False(c.Faction("resistance")!.Archetypes[os.Index]);
        var army = TestContent.Sentinel(1).Players.Single(p => p.Id == 1).Army.Select(e => e.Unit).ToList();
        Assert.Contains("watcher_station", army); // the Sentinel lobby starts with both stations
        Assert.Contains("overseer_station", army);
    }

    // Each case adds one bad file to the MVP content; the error must name
    // the file and the definition at fault.
    public static TheoryData<string, string, string[]> BadContent => new()
    {
        {
            "unknown warhead",
            """{"components":[{"id":"bad_gun","requires":["infantry"],"effects":[{"weapon":{"damage":1,"range":1,"reload":1,"warhead":"plasma"}}]}]}""",
            ["component \"bad_gun\"", "unknown warhead \"plasma\""]
        },
        {
            "unknown component in a slot",
            """{"archetypes":[{"id":"bad_unit","name":"Bad","glyph":"X","tags":["infantry"],"locomotor":"foot","armor":"infantry","hp":1,"speed":1,"turnRate":1,"slots":{"primary":["laser"]}}]}""",
            ["archetype \"bad_unit\"", "unknown component \"laser\" in slots.primary"]
        },
        {
            "slot component requirement unmet",
            """{"archetypes":[{"id":"bad_unit","name":"Bad","glyph":"X","tags":["vehicle"],"locomotor":"wheeled","armor":"light_vehicle","hp":1,"speed":1,"turnRate":1,"slots":{"primary":["rifle"]}}]}""",
            ["archetype \"bad_unit\"", "component \"rifle\" in slots.primary requires tags"]
        },
        {
            "unknown slot",
            """{"archetypes":[{"id":"bad_unit","name":"Bad","glyph":"X","tags":["infantry"],"locomotor":"foot","armor":"infantry","hp":1,"speed":1,"turnRate":1,"slots":{"tertiary":["rifle"]}}]}""",
            ["archetype \"bad_unit\"", "unknown slot \"tertiary\" (want primary or secondary)"]
        },
        {
            "unknown armor class",
            """{"archetypes":[{"id":"bad_unit","name":"Bad","glyph":"X","locomotor":"foot","armor":"heavy","hp":1,"speed":1,"turnRate":1}]}""",
            ["archetype \"bad_unit\"", "unknown armor class \"heavy\" (want one of aircraft, armored_vehicle, building, drone, infantry, light_vehicle, medium_vehicle, parked_aircraft)"]
        },
        {
            "warhead missing an armor class",
            """{"warheads":[{"id":"plasma","versus":{"infantry":100,"light_vehicle":100,"building":100}}]}""",
            ["warhead \"plasma\"", "versus is missing armor class \"aircraft\""]
        },
        {
            "warhead with an unknown armor class",
            """{"warheads":[{"id":"plasma","versus":{"infantry":100,"light_vehicle":100,"building":100,"drone":null,"medium_vehicle":100,"armored_vehicle":100,"parked_aircraft":100,"aircraft":null,"tank":5}}]}""",
            ["warhead \"plasma\"", "unknown armor class \"tank\""]
        },
        {
            "duplicate armor class",
            """{"armorClasses":["drone"]}""",
            ["armor class \"drone\"", "already defined in"]
        },
        {
            "projectile without splash",
            """{"components":[{"id":"bad_gun","effects":[{"weapon":{"damage":1,"range":1,"reload":1,"warhead":"rocket","projectileSpeed":100}}]}]}""",
            ["component \"bad_gun\"", "needs a splash radius"]
        },
        {
            "faction allows unknown archetype",
            """{"factions":[{"id":"bad_side","name":"Bad","archetypes":["mech"],"components":[],"maxPresetsPerArchetype":3,"defeatedWithout":["soldier"]}]}""",
            ["faction \"bad_side\"", "unknown archetype \"mech\""]
        },
        {
            "faction without a defeat rule",
            """{"factions":[{"id":"bad_side","name":"Bad","archetypes":[],"components":[],"maxPresetsPerArchetype":3}]}""",
            ["faction \"bad_side\"", "defeatedWithout needs at least one tag"]
        },
        {
            "placer of unknown archetype",
            """{"components":[{"id":"bad_mine","effects":[{"placer":{"archetype":"claymore","charges":1,"range":10,"maxActive":1}}]}]}""",
            ["component \"bad_mine\"", "unknown archetype \"claymore\""]
        },
        {
            "launch of something that can't fly",
            """{"components":[{"id":"bad_kit","effects":[{"launch":{"archetype":"technical","charges":1,"range":10}}]}]}""",
            ["component \"bad_kit\"", "launches \"technical\", which must be flying"]
        },
        {
            "crew on a structure",
            """{"archetypes":[{"id":"bad_fort","name":"Bad","glyph":"X","locomotor":"none","armor":"building","hp":1,"crew":{"enterRange":1}}]}""",
            ["archetype \"bad_fort\"", "only moving archetypes can have a crew"]
        },
        {
            "unknown tag",
            """{"components":[{"id":"bad_perk","requires":["cyborg"],"effects":[{"modifier":{"stat":"speed","percent":5}}]}]}""",
            ["component \"bad_perk\"", "unknown tag \"cyborg\""]
        },
        {
            "unknown stat",
            """{"components":[{"id":"bad_perk","effects":[{"modifier":{"stat":"luck","percent":5}}]}]}""",
            ["component \"bad_perk\"", "unknown stat \"luck\""]
        },
        {
            "two effect kinds in one effect",
            """{"components":[{"id":"bad_perk","effects":[{"modifier":{"stat":"speed","percent":5},"builder":{"rate":1,"range":1}}]}]}""",
            ["component \"bad_perk\"", "exactly one of"]
        },
        {
            "unlocks an unlocked structure",
            """{"components":[{"id":"bad_assist","effects":[{"buildAssist":{"bonus":1,"radius":1,"unlocks":["supply_depot"]}}]}]}""",
            ["component \"bad_assist\"", "unlocks \"supply_depot\", which is not a locked structure"]
        },
        {
            "duplicate id",
            """{"archetypes":[{"id":"assault","name":"Again","glyph":"X","locomotor":"foot","armor":"infantry","hp":1,"speed":1,"turnRate":1}]}""",
            ["archetype \"assault\"", "already defined in archetypes/infantry.json"]
        },
        {
            "moving structure",
            """{"archetypes":[{"id":"bad_fort","name":"Bad","glyph":"X","locomotor":"tracked","armor":"building","hp":1,"speed":1,"turnRate":1,"footprint":{"w":1,"h":1},"buildTime":1}]}""",
            ["archetype \"bad_fort\"", "footprint"]
        },
        {
            "out of range number",
            """{"archetypes":[{"id":"bad_unit","name":"Bad","glyph":"X","locomotor":"foot","armor":"infantry","hp":0,"speed":1,"turnRate":1}]}""",
            ["archetype \"bad_unit\"", "hp is 0"]
        },
        {
            "misspelled field",
            """{"archetypes":[{"id":"bad_unit","name":"Bad","glyph":"X","locomotor":"foot","armor":"infantry","hitpoints":5}]}""",
            ["invalid JSON", "hitpoints"]
        },
    };

    [Theory]
    [MemberData(nameof(BadContent))]
    public void RejectsBadContent(string name, string json, string[] want)
    {
        var sources = TestContent.MvpSources();
        sources.Add(new ContentSource("extra/bad.json", json));
        var e = Assert.Throws<ContentException>(() => ContentLoader.Load(sources));
        Assert.Contains("extra/bad.json", e.Message);
        foreach (var w in want)
        {
            Assert.True(e.Message.Contains(w, StringComparison.Ordinal), $"{name}: \"{e.Message}\" lacks \"{w}\"");
        }
    }

    [Fact]
    public void RejectsMissingDirectory() =>
        Assert.Throws<ContentException>(() => ContentLoader.LoadDirectory(Path.Combine(TestContent.MvpDir, "nope")));

    // The content hash covers the compiled definitions, not the files: moving
    // definitions between files, reordering them, reformatting and changing
    // line endings all leave it alone.
    [Fact]
    public void HashIgnoresFileLayout()
    {
        var want = TestContent.Mvp.Hash;

        var crlf = TestContent.MvpSources().Select(s => s with { Json = s.Json.Replace("\n", "\r\n") });
        Assert.Equal(want, ContentLoader.Load(crlf).Hash);

        var parse = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip };
        var merged = new JsonObject();
        foreach (var s in TestContent.MvpSources().OrderByDescending(s => s.Name, StringComparer.Ordinal))
        {
            foreach (var (kind, defs) in JsonNode.Parse(s.Json, documentOptions: parse)!.AsObject())
            {
                if (defs is JsonObject single)
                {
                    merged[kind] = single.DeepClone(); // a one-off block (unitAi), not a list
                    continue;
                }
                if (merged[kind] is not JsonArray all)
                {
                    merged[kind] = all = [];
                }
                // Air classes keep their order: a class's position is its value (E5), everything else is reversed.
                foreach (var d in kind == "airClasses" ? defs!.AsArray() : defs!.AsArray().Reverse())
                {
                    all.Add(d!.DeepClone());
                }
            }
        }
        var oneFile = new ContentSource("everything.json", merged.ToJsonString());
        Assert.Equal(want, ContentLoader.Load([oneFile]).Hash);

        var halves = TestContent.MvpSources().Select(s => s with { Name = "z/" + s.Name }).ToList();
        Assert.Equal(want, ContentLoader.Load(halves).Hash);
    }

    [Fact]
    public void HashChangesWithContent()
    {
        var sources = TestContent.MvpSources()
            .Select(s => s.Name == "archetypes/infantry.json" ? s with { Json = s.Json.Replace("\"hp\": 60", "\"hp\": 61") } : s)
            .ToList();
        Assert.Contains(sources, s => s.Json.Contains("\"hp\": 61"));
        Assert.NotEqual(TestContent.Mvp.Hash, ContentLoader.Load(sources).Hash);
    }

    public static TheoryData<long, int[], long[], long> PipelineCases => new()
    {
        { 100, [], [], 100 },
        { 100, [20, -10], [5], 115 },     // 100 × 110 / 100 + 5
        { 100, [-10, 20], [5], 115 },     // order never matters
        { 51, [15], [0], 58 },            // integer, truncated: 51 × 115 / 100 = 58.65
        { 0, [50], [7], 0 },              // a zero base stays zero
        { 40, [-100], [0], 1 },           // reload and max HP never drop below 1
    };

    [Theory]
    [MemberData(nameof(PipelineCases))]
    public void StatPipelineFormula(long baseValue, int[] percents, long[] flats, long want)
    {
        var mods = percents.Select(p => new Modifier(Stat.Reload, p, 0)).Concat(flats.Select(f => new Modifier(Stat.Reload, 0, f)));
        var bases = new long[StatPipeline.StatCount];
        bases[(int)Stat.Reload] = baseValue;
        Assert.Equal(want, StatPipeline.Compile(bases, mods)[(int)Stat.Reload]);
        Assert.Equal(want, StatPipeline.Compile(bases, mods.Reverse())[(int)Stat.Reload]);
    }

    [Fact]
    public void StatPipelineClampsAtZero() => Assert.Equal(0, StatPipeline.Apply(Stat.Speed, 50, -150, 0));
}
