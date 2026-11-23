using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The helicopters' rocket pods against vehicles (WPN-2, owner 2026-11-23: "2 half a truck yes"): before, one salvo of 4 rockets
// (60 x 120% = 72 each, 288) killed any truck of 288 HP or less. Through pod_rocket a hit on a vehicle of 150 HP or more takes at
// most 13% of its max HP (maxHpPercent as a cap), so a full salvo takes about half of it (50-52%) and never kills it, and two kill
// it. Under 150 HP (the dirt bike) and what is not a vehicle (the stations, the crate, the antenna tower) take the rocket's 72 as
// before (the coordinator: nothing small gets tougher). Soldiers, buildings, armour and parked aircraft take what the rocket gave
// them. Only the pods use the warhead, so no other weapon's numbers move.
public class HeliRocketPodTests(ITestOutputHelper output)
{
    private const int Salvo = 4;

    private static bool IsPod(WeaponDef x) => x.Warhead.Id == "pod_rocket";

    [Fact]
    public void OnlyThePodsUseTheirWarhead()
    {
        var c = TestContent.Mvp;
        var users = c.Components.Where(k => k.Effects.OfType<WeaponEffect>().Any(e => e.Warhead.Id == "pod_rocket")).Select(k => k.Id).ToList();
        Assert.Equal(["heli_rocket_pods"], users);
    }

    [Fact]
    public void ASalvoTakesAboutHalfATruckAndNeverKillsIt()
    {
        var w = Arena.NewSentinel(128);
        var light = w.Content.ArmorClass("light_vehicle");
        var medium = w.Content.ArmorClass("medium_vehicle");
        var soft = w.Content.Archetypes.Where(a => a.Armor == light || a.Armor == medium).Select(a => a.Id).ToList();
        var pods = w.Player(1)!.Setup.Find(w.Content, "attack_helicopter")!.Weapons.Single(IsPod);
        var rocket = w.Content.Warhead("rocket")!;
        Assert.Equal((60, Salvo, true, 150), (pods.Damage, pods.Effect.SalvoSize, pods.Warhead.MaxHpCap, pods.Warhead.MaxHpFrom));
        var (trucks, small) = (new List<string>(), new List<string>());
        for (var i = 0; i < soft.Count; i++)
        {
            var id = soft[i];
            if ((w.Player(2)!.Setup.Find(w.Content, id) ?? w.Player(1)!.Setup.Find(w.Content, id)) is not { } def)
            {
                continue; // neither side fields it
            }
            var t = w.Unit(w.Spawn(def, new Cell(4 + i % 12 * 10, 4 + i / 12 * 10), Facing.North))!;
            var max = t.Def.MaxHp;
            for (var k = 0; k < Salvo; k++)
            {
                w.Hurt(t, pods.Warhead, pods.Damage, 100);
            }
            if (max < 150 || !def.Tags.Matches(pods.Warhead.MaxHpTags))
            {
                // Under 150 HP, or not a vehicle: the rocket's damage, as before WPN-2.
                small.Add($"{id} {max}");
                Assert.Equal(Math.Max(0, max - Salvo * rocket.Damage(60, t.Def.Armor, 100)), Math.Max(0, t.HP));
                continue;
            }
            var share = (max - t.HP) * 100 / max;
            trucks.Add($"{id} {max}: {share}%");
            Assert.True(t.HP > 0, $"{id} ({max} HP) died to one salvo");
            Assert.Equal(max - Salvo * (max * 13 / 100), t.HP);
            Assert.InRange(share, 50, 52);
            for (var k = 0; k < Salvo; k++)
            {
                w.Hurt(t, pods.Warhead, pods.Damage, 100);
            }
            Assert.True(t.HP <= 0, $"{id} ({max} HP) survived two salvos");
        }
        output.WriteLine($"trucks: {string.Join("; ", trucks)}");
        output.WriteLine($"as before: {string.Join("; ", small)}");
        Assert.True(trucks.Count >= 33, $"only {trucks.Count} trucks tried");
    }

    // The coordinator: the pods make no small soft thing tougher. Rockets to kill, as before WPN-2 (72 a rocket): the 60-HP drone and
    // scout stations 1, the 80-HP Kaman station and the 90-HP dirt bike 2, the supply crate and the antenna tower (150 HP, not
    // vehicles) 3; a truck of 150 HP or more 8 (the light rocket truck, 150, was 3; the technical, 180, was 3).
    [Theory]
    [InlineData("drone_station", 1)]
    [InlineData("scout_station", 1)]
    [InlineData("kaman_station", 2)]
    [InlineData("dirt_bike", 2)]
    [InlineData("supply_crate", 3)]
    [InlineData("antenna_tower", 3)]
    [InlineData("light_rocket_truck", 8)]
    [InlineData("technical", 8)]
    [InlineData("hypersonic_truck", 8)]
    public void RocketsToKill(string id, int rockets)
    {
        var w = Arena.NewSentinel(64);
        var pods = w.Player(1)!.Setup.Find(w.Content, "attack_helicopter")!.Weapons.Single(IsPod);
        var def = w.Player(2)!.Setup.Find(w.Content, id) ?? w.Player(1)!.Setup.Find(w.Content, id) ?? throw new ArgumentException(id);
        var t = w.Unit(w.Spawn(def, new Cell(32, 32), Facing.North))!;
        var n = 0;
        for (; n < 20 && t.HP > 0; n++)
        {
            w.Hurt(t, pods.Warhead, pods.Damage, 100);
        }
        Assert.Equal(rockets, n);
    }

    [Fact]
    public void TheCapsRulesNeedAShareAndKnownTags()
    {
        static string Refused(string from, string to)
        {
            Assert.Contains(from, TestContent.MvpSources().Single(s => s.Name == "warheads.json").Json);
            var sources = TestContent.MvpSources()
                .Select(s => s.Name == "warheads.json" ? s with { Json = s.Json.Replace(from, to, StringComparison.Ordinal) } : s)
                .ToList();
            return Assert.Throws<SimCore.Content.ContentException>(() => SimCore.Content.ContentLoader.Load(sources)).Message;
        }
        Assert.Contains("unknown tag \"lorry\" in maxHpTags", Refused("\"maxHpTags\": [\"vehicle\"]", "\"maxHpTags\": [\"lorry\"]"));
        Assert.Contains("maxHpCap, maxHpFrom and maxHpTags need a maxHpPercent",
            Refused("\"maxHpPercent\": { \"light_vehicle\": 13, \"medium_vehicle\": 13 }, \"maxHpBySplash\": true, ", ""));
    }

    [Fact]
    public void ANeighbourInTheSplashTakesLess()
    {
        var w = Arena.NewSentinel(64);
        var pods = w.Player(1)!.Setup.Find(w.Content, "attack_helicopter")!.Weapons.Single(IsPod);
        var t = w.Spawn(2, "cargo_truck", 32, 32);
        w.Hurt(t, pods.Warhead, pods.Damage, 30); // the splash's edge
        Assert.Equal(t.Def.MaxHp - t.Def.MaxHp * (13 * 30 / 100) / 100, t.HP);
    }

    [Fact]
    public void SoldiersBuildingsAndArmourTakeWhatTheRocketGave()
    {
        var c = TestContent.Mvp;
        var (pod, rocket) = (c.Warhead("pod_rocket")!, c.Warhead("rocket")!);
        Assert.Equal(rocket.Versus, pod.Versus);
        foreach (var armor in new[] { "infantry", "building", "armored_vehicle", "parked_aircraft" })
        {
            var a = c.ArmorClass(armor);
            Assert.Equal(0, pod.MaxHpPercentOf(a));
            foreach (var pct in new[] { 100, 30 })
            {
                Assert.Equal(rocket.Damage(60, a, pct), pod.Damage(60, a, pct));
            }
        }
        Assert.Equal(24, pod.Damage(60, c.ArmorClass("infantry"), 100)); // a rifleman's 60 HP in three direct hits, as before
    }
}
