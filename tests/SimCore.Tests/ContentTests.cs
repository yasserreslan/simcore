using System.Text.Json;
using System.Text.Json.Nodes;
using SimCore.Content;
using SimCore.Model;

namespace SimCore.Tests;

public class ContentTests
{
    [Fact]
    public void SampleLoads()
    {
        var c = TestContent.Sample;
        Assert.Equal(15, c.Archetypes.Count);
        Assert.Equal(16, c.Components.Count);
        Assert.Equal(["faction", "resistance"], c.Factions.Select(f => f.Id));
        Assert.Equal(4, c.Warheads.Count);
        Assert.Matches("^[0-9a-f]{64}$", c.Hash);

        var drone = c.Archetype("drone")!;
        Assert.Equal(Layers.Air, drone.Layer);
        Assert.Equal(Layers.Ground, c.Archetype("battle_tank")!.Layer);
        Assert.True(c.Archetype("reinforced_bunker")!.Locked);
        Assert.True(c.Archetype("booby_trap")!.Hidden);
        Assert.Equal((3, 2), (c.Archetype("barracks")!.FootprintW, c.Archetype("barracks")!.FootprintH));
        var assist = Assert.IsType<BuildAssistEffect>(Assert.Single(c.Component("construction_assist")!.Effects));
        Assert.Equal([c.Archetype("reinforced_bunker")!.Index], assist.Unlocks);
        Assert.False(c.Faction("resistance")!.Archetypes[drone.Index]);
        Assert.True(c.Faction("faction")!.Archetypes[drone.Index]);
    }

    // Each case adds one bad file to the sample content; the error must name
    // the file and the definition at fault.
    public static TheoryData<string, string, string[]> BadContent => new()
    {
        {
            "unknown warhead",
            """{"components":[{"id":"bad_gun","slot":"weapon","requires":["infantry"],"effects":[{"weapon":{"damage":1,"range":1,"reload":1,"warhead":"plasma"}}]}]}""",
            ["component \"bad_gun\"", "unknown warhead \"plasma\""]
        },
        {
            "unknown default component",
            """{"archetypes":[{"id":"bad_unit","name":"Bad","glyph":"X","tags":["infantry"],"locomotor":"foot","armor":"none","hp":1,"speed":1,"turnRate":1,"defaultComponents":["laser"]}]}""",
            ["archetype \"bad_unit\"", "unknown component \"laser\""]
        },
        {
            "default component requirement unmet",
            """{"archetypes":[{"id":"bad_unit","name":"Bad","glyph":"X","tags":["vehicle"],"locomotor":"wheeled","armor":"light","hp":1,"speed":1,"turnRate":1,"defaultComponents":["rifle"]}]}""",
            ["archetype \"bad_unit\"", "default component \"rifle\" requires tags"]
        },
        {
            "faction allows unknown archetype",
            """{"factions":[{"id":"bad_side","name":"Bad","archetypes":["mech"],"components":[]}]}""",
            ["faction \"bad_side\"", "unknown archetype \"mech\""]
        },
        {
            "placer of unknown archetype",
            """{"components":[{"id":"bad_mine","slot":"ability","effects":[{"placer":{"archetype":"claymore","charges":1,"range":10}}]}]}""",
            ["component \"bad_mine\"", "unknown archetype \"claymore\""]
        },
        {
            "unknown tag",
            """{"components":[{"id":"bad_perk","slot":"perk","requires":["cyborg"],"effects":[{"modifier":{"stat":"speed","percent":5}}]}]}""",
            ["component \"bad_perk\"", "unknown tag \"cyborg\""]
        },
        {
            "unknown stat",
            """{"components":[{"id":"bad_perk","slot":"perk","effects":[{"modifier":{"stat":"luck","percent":5}}]}]}""",
            ["component \"bad_perk\"", "unknown stat \"luck\""]
        },
        {
            "unknown slot",
            """{"components":[{"id":"bad_perk","slot":"hat","effects":[{"modifier":{"stat":"speed","percent":5}}]}]}""",
            ["component \"bad_perk\"", "unknown slot \"hat\""]
        },
        {
            "two effect kinds in one effect",
            """{"components":[{"id":"bad_perk","slot":"perk","effects":[{"modifier":{"stat":"speed","percent":5},"builder":{"rate":1,"range":1}}]}]}""",
            ["component \"bad_perk\"", "exactly one of"]
        },
        {
            "unlocks an unlocked structure",
            """{"components":[{"id":"bad_assist","slot":"ability","effects":[{"buildAssist":{"bonus":1,"radius":1,"unlocks":["barracks"]}}]}]}""",
            ["component \"bad_assist\"", "unlocks \"barracks\", which is not a locked structure"]
        },
        {
            "duplicate id",
            """{"archetypes":[{"id":"rifleman","name":"Again","glyph":"X","locomotor":"foot","armor":"none","hp":1,"speed":1,"turnRate":1}]}""",
            ["archetype \"rifleman\"", "already defined in archetypes/infantry.json"]
        },
        {
            "moving structure",
            """{"archetypes":[{"id":"bad_fort","name":"Bad","glyph":"X","locomotor":"tracked","armor":"building","hp":1,"speed":1,"turnRate":1,"footprint":{"w":1,"h":1},"buildTime":1}]}""",
            ["archetype \"bad_fort\"", "footprint"]
        },
        {
            "out of range number",
            """{"archetypes":[{"id":"bad_unit","name":"Bad","glyph":"X","locomotor":"foot","armor":"none","hp":0,"speed":1,"turnRate":1}]}""",
            ["archetype \"bad_unit\"", "hp is 0"]
        },
        {
            "misspelled field",
            """{"archetypes":[{"id":"bad_unit","name":"Bad","glyph":"X","locomotor":"foot","armor":"none","hitpoints":5}]}""",
            ["invalid JSON", "hitpoints"]
        },
    };

    [Theory]
    [MemberData(nameof(BadContent))]
    public void RejectsBadContent(string name, string json, string[] want)
    {
        var sources = TestContent.SampleSources();
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
        Assert.Throws<ContentException>(() => ContentLoader.LoadDirectory(Path.Combine(TestContent.SampleDir, "nope")));

    // The content hash covers the compiled definitions, not the files: moving
    // definitions between files, reordering them, reformatting and changing
    // line endings all leave it alone.
    [Fact]
    public void HashIgnoresFileLayout()
    {
        var want = TestContent.Sample.Hash;

        var crlf = TestContent.SampleSources().Select(s => s with { Json = s.Json.Replace("\n", "\r\n") });
        Assert.Equal(want, ContentLoader.Load(crlf).Hash);

        var parse = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip };
        var merged = new JsonObject();
        foreach (var s in TestContent.SampleSources().OrderByDescending(s => s.Name, StringComparer.Ordinal))
        {
            foreach (var (kind, defs) in JsonNode.Parse(s.Json, documentOptions: parse)!.AsObject())
            {
                if (merged[kind] is not JsonArray all)
                {
                    merged[kind] = all = [];
                }
                foreach (var d in defs!.AsArray().Reverse())
                {
                    all.Add(d!.DeepClone());
                }
            }
        }
        var oneFile = new ContentSource("everything.json", merged.ToJsonString());
        Assert.Equal(want, ContentLoader.Load([oneFile]).Hash);

        var halves = TestContent.SampleSources().Select(s => s with { Name = "z/" + s.Name }).ToList();
        Assert.Equal(want, ContentLoader.Load(halves).Hash);
    }

    [Fact]
    public void HashChangesWithContent()
    {
        var sources = TestContent.SampleSources()
            .Select(s => s.Name == "archetypes/infantry.json" ? s with { Json = s.Json.Replace("\"hp\": 50", "\"hp\": 51") } : s)
            .ToList();
        Assert.Contains(sources, s => s.Json.Contains("\"hp\": 51"));
        Assert.NotEqual(TestContent.Sample.Hash, ContentLoader.Load(sources).Hash);
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
