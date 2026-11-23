using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The helicopters' rocket pods against vehicles (WPN-2, owner 2026-11-23: "2 half a truck yes"): before, one salvo of 4 rockets
// (60 x 120% = 72 each, 288) killed any truck of 288 HP or less. Through pod_rocket a light or medium vehicle loses 13% of its max
// HP to a direct hit (maxHpPercent), so a full salvo takes about half of it (52%; 47% of a 60-HP station by rounding) and never
// kills it, and two kill it (under 100 HP, the dirt bike and the 60-HP stations, the rounding leaves a few HP for a ninth rocket).
// Soldiers, buildings, armour and parked aircraft take what the rocket gave them. Only the pods use the warhead, so no other
// weapon's numbers move.
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
    public void ASalvoTakesAboutHalfASoftVehicleAndNeverKillsIt()
    {
        var w = Arena.NewSentinel(128);
        var light = w.Content.ArmorClass("light_vehicle");
        var medium = w.Content.ArmorClass("medium_vehicle");
        var soft = w.Content.Archetypes.Where(a => a.Armor == light || a.Armor == medium).Select(a => a.Id).ToList();
        var pods = w.Player(1)!.Setup.Find(w.Content, "attack_helicopter")!.Weapons.Single(IsPod);
        Assert.Equal((60, Salvo), (pods.Damage, pods.Effect.SalvoSize));
        var tried = new List<string>();
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
            var share = (max - t.HP) * 100 / max;
            tried.Add($"{id} {max}: {share}%");
            Assert.True(t.HP > 0, $"{id} ({max} HP) died to one salvo");
            Assert.Equal(max - Salvo * (max * 13 / 100), t.HP);
            Assert.InRange(share, 45, 52);
            for (var k = 0; k < Salvo; k++)
            {
                w.Hurt(t, pods.Warhead, pods.Damage, 100);
            }
            Assert.True(max < 100 || t.HP <= 0, $"{id} ({max} HP) survived two salvos"); // under 100 HP the share rounds down

            w.Hurt(t, pods.Warhead, pods.Damage, 100);
            Assert.True(t.HP <= 0, $"{id} ({max} HP) survived two salvos and a rocket");
        }
        output.WriteLine(string.Join("; ", tried));
        Assert.True(tried.Count > 40, $"only {tried.Count} tried");
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
