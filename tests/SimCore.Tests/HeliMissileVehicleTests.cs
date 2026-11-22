using SimCore.FixedPoint;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

// The helicopters' missiles against vehicles (WPN-1, owner 2026-11-22: "attack shoppers are way too powerful against vehicles it
// should take at least 2 missiles to destroy a vehicle completely, depending on the vehicle"): through heli_missile a light or
// medium vehicle loses 55% of its max HP to a direct hit (maxHpPercent), so one missile never kills it and two always do; armour
// takes the weapon's damage on its roof (the helicopter's hit is top). Only the two helicopter missiles use the warhead, so no
// other weapon's numbers move.
public class HeliMissileVehicleTests(ITestOutputHelper output)
{
    private static readonly string[] Missiles = ["heli_missiles", "heli_missiles_light"];

    private static bool IsMissile(WeaponDef x) => x.Warhead.Id == "heli_missile";

    [Fact]
    public void OnlyTheHelicoptersMissilesUseTheirWarhead()
    {
        var c = TestContent.Mvp;
        var users = c.Components.Where(k => k.Effects.OfType<WeaponEffect>().Any(e => e.Warhead.Id == "heli_missile")).Select(k => k.Id).Order().ToList();
        Assert.Equal(Missiles, users);
    }

    [Fact]
    public void OneMissileNeverKillsASoftVehicleAndTwoAlwaysDo()
    {
        var w = Arena.NewSentinel(128);
        var light = w.Content.ArmorClass("light_vehicle");
        var medium = w.Content.ArmorClass("medium_vehicle");
        var soft = w.Content.Archetypes.Where(a => a.Armor == light || a.Armor == medium).Select(a => a.Id).ToList();
        Assert.True(soft.Count > 40, $"{soft.Count} soft vehicles");
        var tried = 0;
        foreach (var heli in new[] { "attack_helicopter", "armored_helicopter" })
        {
            var m = w.Player(1)!.Setup.Find(w.Content, heli)!.Weapons.Single(IsMissile);
            for (var i = 0; i < soft.Count; i++)
            {
                var id = soft[i];
                if ((w.Player(2)!.Setup.Find(w.Content, id) ?? w.Player(1)!.Setup.Find(w.Content, id)) is not { } def)
                {
                    continue; // neither side fields it (the antenna tower is the network's, spawned)
                }
                var t = w.Unit(w.Spawn(def, new Cell(4 + i % 12 * 10, 4 + i / 12 * 10), Facing.North))!;
                tried++;
                w.Hurt(t, m.Warhead, m.Damage, 100);
                Assert.True(t.HP > 0, $"{id} ({t.Def.MaxHp} HP) died to one of the {heli}'s missiles");
                Assert.Equal(t.Def.MaxHp - t.Def.MaxHp * 55 / 100, t.HP);
                w.Hurt(t, m.Warhead, m.Damage, 100);
                Assert.True(t.HP <= 0, $"{id} survived two");
            }
        }
        output.WriteLine($"{tried / 2} of {soft.Count} soft vehicles fielded and tried");
        Assert.True(tried / 2 > 40, $"only {tried / 2} tried");
    }

    [Fact]
    public void ANeighbourInTheSplashTakesLess()
    {
        var w = Arena.NewSentinel(64);
        var m = w.Player(1)!.Setup.Find(w.Content, "attack_helicopter")!.Weapons.Single(IsMissile);
        var t = w.Spawn(2, "cargo_truck", 32, 32);
        w.Hurt(t, m.Warhead, m.Damage, 60); // 60% of the hit: the splash's fall-off
        Assert.Equal(t.Def.MaxHp - t.Def.MaxHp * 55 * 60 / 10000, t.HP);
    }

    [Fact]
    public void TheAttackHelicoptersFirstMissileLeavesATechnicalStanding()
    {
        // In a fight (its cannon fires too): the first missile to hit leaves it standing.
        var w = Arena.NewSentinel(128);
        var u = w.Spawn(1, "attack_helicopter", 20, 64, Facing.East);
        w.Step();
        var t = w.Spawn(2, "technical", 80, 64);
        w.Issue(new Command { Tick = w.Tick, Player = 2, Kind = CommandKind.SetStance, Units = [t.Id], Stance = Stance.HoldFire });
        w.Issue(new Command { Tick = w.Tick, Player = 1, Kind = CommandKind.Attack, Units = [u.Id], Target = t.Id });
        var hits = new List<int>();
        var missiles = new List<uint>();
        for (var i = 0; i < 1200 && t.Alive; i++)
        {
            w.Step();
            missiles.AddRange(w.Projectiles.Where(p => p.Shooter == u.Id && IsMissile(p.Weapon) && !missiles.Contains(p.Id)).Select(p => p.Id));
            hits.AddRange(w.Detonations.Where(d => d.Hit == t.Id && missiles.Contains(d.Projectile)).Select(_ => t.HP));
        }
        output.WriteLine($"HP after each missile: {string.Join(", ", hits)}");
        Assert.False(t.Alive);
        Assert.NotEmpty(hits);
        Assert.True(hits[0] > 0, "the first missile leaves it standing");
    }
}
