using SimCore.Sim;

namespace SimCore.Tests;

// LegacyContentTests checks that the sample content reproduces the
// hard-coded unit list it replaces, stat for stat.
public class LegacyContentTests
{
    [Fact]
    public void SampleReproducesUnitTypes()
    {
        var c = TestContent.Sample;
        foreach (var id in Enum.GetValues<UnitTypeId>())
        {
            var old = UnitTypes.Of(id);
            var a = c.Archetype(old.Key);
            Assert.NotNull(a);
            Assert.Equal((old.Name, old.Glyph, old.Cost, old.MaxHP, old.Speed, old.TurnRate, (int)old.Armor, old.Turret),
                (a.Name, a.Glyph, a.Cost, a.MaxHp, a.Speed, a.TurnRate, (int)a.Armor, a.Turret));
            var weapons = a.DefaultComponents.SelectMany(i => c.Components[i].Effects).OfType<Model.WeaponEffect>().ToList();
            if (old.Weapon is null)
            {
                Assert.Empty(weapons);
                continue;
            }
            var w = Assert.Single(weapons);
            Assert.Equal((old.Weapon.Damage, old.Weapon.Range, old.Weapon.ReloadTicks, old.Weapon.Splash, old.Weapon.Scatter),
                (w.Damage, w.Range, w.Reload, w.Splash, w.Scatter));
            foreach (var armor in Enum.GetValues<Armor>())
            {
                Assert.Equal(UnitTypes.Damage(old.Weapon, armor, 100), w.Warhead.Damage(w.Damage, (Model.Armor)armor, 100));
                Assert.Equal(UnitTypes.Damage(old.Weapon, armor, 63), w.Warhead.Damage(w.Damage, (Model.Armor)armor, 63));
            }
        }
    }
}
