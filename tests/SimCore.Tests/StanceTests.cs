using SimCore.Content;
using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

// The five stances, their content defaults and the unitAi rules block.
public class StanceTests
{
    private static void SetStance(World w, Unit u, Stance s) =>
        w.Issue(new Command { Tick = w.Tick, Player = u.Owner, Kind = CommandKind.SetStance, Units = [u.Id], Stance = s });

    [Theory]
    [InlineData("assault", true)]
    [InlineData("sniper", true)]
    [InlineData("kornet", true)]
    [InlineData("technical", true)]
    [InlineData("dirt_bike", true)]
    [InlineData("engineer", false)]      // a pistol: never chases
    [InlineData("worker", false)]
    [InlineData("manpads", false)]       // air defence: watches the sky
    [InlineData("sam_vehicle", false)]
    [InlineData("mortar", false)]        // sets up to fire
    [InlineData("light_rocket_truck", false)]
    [InlineData("medium_rocket_launcher", false)]
    [InlineData("heavy_rocket_launcher", false)]
    [InlineData("missile_truck", false)]
    [InlineData("ballistic_truck", false)]
    [InlineData("rocket_jeep", false)]
    [InlineData("drone_truck", false)]
    public void PursueDefaults(string archetype, bool pursue)
    {
        var a = TestContent.Mvp.Archetype(archetype)!;
        Assert.Equal(pursue, a.Pursue);
        Assert.Equal((int)Stance.Guard, a.Stance); // every unit starts on Guard
    }

    [Fact]
    public void TheRulesBlockCarriesTheZeroHourNumbers()
    {
        var r = TestContent.Mvp.UnitAi;
        Assert.Equal(180, r.GuardInnerPercent);
        Assert.Equal(220, r.GuardOuterPercent);
        Assert.Equal(133, r.AggressivePercent);
        Assert.Equal(300, r.ChaseTicks);
        Assert.Equal(10, r.ScanTicks);
        Assert.Equal(20, r.ReturnScanTicks);
        Assert.Equal(Fixed.FromInt(20), r.RetaliateMax);
        Assert.Equal(Fixed.FromInt(8), r.RetaliateFriends);
        Assert.Equal(20, r.RetaliateCooldownTicks);
        Assert.Equal(UnitAiRules.Default, r); // the file states the defaults
    }

    [Fact]
    public void TheRulesBlockIsTunableAndHashed()
    {
        var sources = TestContent.MvpSources()
            .Select(s => s.Name == "unit_ai.json" ? s with { Json = s.Json.Replace("\"chaseTicks\": 300", "\"chaseTicks\": 200") } : s)
            .ToList();
        var c = ContentLoader.Load(sources);
        Assert.Equal(200, c.UnitAi.ChaseTicks);
        Assert.NotEqual(TestContent.Mvp.Hash, c.Hash);
        var none = TestContent.MvpSources().Where(s => s.Name != "unit_ai.json").ToList();
        Assert.Equal(UnitAiRules.Default, ContentLoader.Load(none).UnitAi);
        Assert.Equal(TestContent.Mvp.Hash, ContentLoader.Load(none).Hash); // the defaults, stated or not
    }

    [Fact]
    public void TwoRulesBlocksOrABadStanceAreErrors()
    {
        var twice = TestContent.MvpSources();
        twice.Add(twice.Single(s => s.Name == "unit_ai.json") with { Name = "more/unit_ai.json" });
        Assert.Contains("unitAi: already defined", Assert.Throws<ContentException>(() => ContentLoader.Load(twice)).Message);
        var bad = TestContent.MvpSources()
            .Select(s => s.Name == "archetypes/infantry.json" ? s with { Json = s.Json.Replace("\"pursue\": false,", "\"stance\": \"berserk\",") } : s)
            .ToList();
        Assert.Contains("unknown stance", Assert.Throws<ContentException>(() => ContentLoader.Load(bad)).Message);
    }

    [Fact]
    public void AnArchetypeMayStartInAnotherStance()
    {
        var sources = TestContent.MvpSources()
            .Select(s => s.Name == "archetypes/infantry.json" ? s with { Json = s.Json.Replace("\"pursue\": false,", "\"pursue\": false, \"stance\": \"holdFire\",") } : s)
            .ToList();
        Assert.Equal((int)Stance.HoldFire, ContentLoader.Load(sources).Archetype("engineer")!.Stance);
    }

    [Theory]
    [InlineData(Stance.Guard)]
    [InlineData(Stance.HoldFire)]
    [InlineData(Stance.ReturnFire)]
    [InlineData(Stance.HoldGround)]
    [InlineData(Stance.Aggressive)]
    public void SetStanceTakesEveryStance(Stance s)
    {
        var w = Arena.New();
        var u = w.Spawn(1, "Rifleman", 5, 5);
        SetStance(w, u, s);
        w.Run(1);
        Assert.Equal(s, u.Stance);
        SetStance(w, u, (Stance)99);
        w.Run(1);
        Assert.Equal(s, u.Stance); // an unknown stance is ignored
    }

    [Fact]
    public void HoldGroundFiresInReachAndNeverMoves()
    {
        var w = Arena.New(40);
        var u = w.Spawn(1, "Rifleman", 10, 10);
        SetStance(w, u, Stance.HoldGround);
        var near = w.Spawn(2, "Rifleman", 10, 14); // 4 cells: in reach (5)
        var far = w.Spawn(2, "Marksman", 10, 22);  // 12 cells: out of reach
        near.Stance = Stance.HoldFire;
        far.Stance = Stance.HoldFire;
        var start = u.Pos;
        w.Run(200);
        Assert.Equal(start, u.Pos);
        Assert.True(!near.Alive || near.HP < near.Def.MaxHp);
        Assert.Equal(far.Def.MaxHp, far.HP);
    }
}
